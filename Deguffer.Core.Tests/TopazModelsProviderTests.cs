using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The machine-wide Topaz Labs model folders. What has to hold is that only a product's
/// <c>models</c> folder ever goes, never the product's folder or the vendor's; that a product is
/// called uninstalled only on complete evidence; and that a product which may still be installed
/// never loses the sign-in record kept beside its models.
/// </summary>
public sealed class TopazModelsProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeUninstallRegistry _registry = new();

    public TopazModelsProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Combine(_system.ProgramData, "Topaz Labs LLC");

    private string Product(string name) => Path.Combine(Root, name);

    private string Models(string name) => Path.Combine(Product(name), "models");

    private string ProgramFolder => Path.Combine(_system.ProgramFiles, "Topaz Labs LLC");

    private TopazModelsProvider CreateProvider(FakeProcessInspector? inspector = null, FakeLiveTreeInspector? liveTrees = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            registry: _registry,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    private static string Write(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>A product folder with models and the logs Topaz keeps beside them.</summary>
    private string CreateProduct(string name, bool withSignIn = false)
    {
        Write(Path.Combine(Models(name), "ssdunet-v1-fp16-64x64-ov.tz2"));
        Write(Path.Combine(Product(name), "Logs", "main.log"), 64);

        if (withSignIn)
        {
            Write(Path.Combine(Models(name), "auth.tz"), 64);
        }

        return Models(name);
    }

    private void Install(string name) =>
        Directory.CreateDirectory(Path.Combine(_system.ProgramFiles, "Topaz Labs LLC", name));

    [Fact]
    public async Task ReportsNotPresentWhereNoTopazFolderExists()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task AnEmptyModelsFolderIsNotPresence()
    {
        Directory.CreateDirectory(Models("Topaz Photo AI"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// The issue's best target: a product nothing installed any more. The whole <c>models</c> folder
    /// goes, sign-in record included, because nothing will read it again, and it is listed under its
    /// own heading.
    /// </summary>
    [Fact]
    public async Task OffersTheWholeModelsFolderOfAProductThatIsNoLongerInstalled()
    {
        var models = CreateProduct("Topaz DeNoise AI", withSignIn: true);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([models], plan.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.False(plan.RequiresElevation);

        var step = Assert.Single(plan.Steps.OfType<DeleteStep>());
        Assert.Equal("No longer installed", step.Group);
        Assert.Equal("Topaz DeNoise AI", step.Identity?.Key);
        Assert.Null(step.UseCheck);
    }

    /// <summary>§5.6: the run takes the models and nothing beside them or above them.</summary>
    [Fact]
    public async Task ARunRemovesOnlyTheModelsAndVerifiesTheRestSurvived()
    {
        var models = CreateProduct("Topaz Sharpen AI");
        var stranger = Write(Path.Combine(Root, "Shared", "settings.json"), 64);
        var loose = Write(Path.Combine(Root, "readme.txt"), 64);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(models));

        string[] mustSurvive =
        [
            Root,
            Product("Topaz Sharpen AI"),
            Path.Combine(Product("Topaz Sharpen AI"), "Logs", "main.log"),
            stranger,
            loose,
        ];

        Assert.All(mustSurvive, path => Assert.True(Directory.Exists(path) || File.Exists(path), $"{path} was removed"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(Product("Topaz Sharpen AI"), "Logs"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(Root, "Shared"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.2's unrecognised case: a folder in the vendor's that is not named for a product is never reached.</summary>
    [Fact]
    public async Task LeavesAFolderNotNamedForAProductAlone()
    {
        var stranger = Path.Combine(Root, "Shared", "models");
        Write(Path.Combine(stranger, "model.tz"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a folder Topaz names", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersAnInstalledProductsModelsWhereNoSignInRecordIsBesideThem()
    {
        var models = CreateProduct("Topaz Gigapixel AI");
        Install("Topaz Gigapixel AI");

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteStep>());
        Assert.Equal(models, step.Path);
        Assert.Equal("Installed", step.Group);
        Assert.NotNull(step.UseCheck);
    }

    /// <summary>
    /// Every file a product keeps beside its models holds the folder back on its own, not only the
    /// sign-in record the other tests use.
    /// </summary>
    [Theory]
    [InlineData("auth.tpz")]
    [InlineData("tvai.tz")]
    public async Task EachKeptFileHoldsAnInstalledProductsModelsFolder(string kept)
    {
        var models = CreateProduct("Topaz Video AI");
        var file = Write(Path.Combine(models, kept), 64);
        Install("Topaz Video AI");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == file);
    }

    /// <summary>
    /// A sign-in record one level down holds the outer folder back as well: removing the outer folder
    /// takes everything inside it.
    /// </summary>
    [Fact]
    public async Task ASignInRecordInTheInnerModelsFolderHoldsTheOuterOneBack()
    {
        var models = CreateProduct("Topaz Video");
        var auth = Write(Path.Combine(models, "models", "auth.tz"), 64);
        Write(Path.Combine(models, "models", "prob-v4-fp16-ov.tz"));
        Install("Topaz Video");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == auth);
    }

    /// <summary>
    /// An inner <c>models</c> that is a link was never examined, so the outer folder that holds it
    /// stays, and nothing behind the link is reached.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task AnInnerModelsFolderThatIsALinkHoldsTheOuterOneBack(DirectoryLinkKind kind)
    {
        var models = CreateProduct("Topaz Video");
        var outside = _temp.CreateDirectory("elsewhere");
        var auth = Write(Path.Combine(outside, "auth.tz"), 64);
        DirectoryLink.Create(kind, Path.Combine(models, "models"), outside);
        Install("Topaz Video");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(File.Exists(auth));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(models, "models"));
    }

    /// <summary>
    /// The sign-in record Topaz Video AI keeps at the top of <c>models</c>. While the product may be
    /// installed the folder holding it is never removed whole, and the record is asserted to survive.
    /// </summary>
    [Fact]
    public async Task KeepsAnInstalledProductsModelsFolderThatHoldsItsSignInRecord()
    {
        var models = CreateProduct("Topaz Video AI", withSignIn: true);
        Install("Topaz Video AI");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == models);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(models, "auth.tz"));
        Assert.Contains(plan.Notes, n => n.Message.Contains("sign-in record", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sign-in record above an inner <c>models</c> folder, which is where the reported files would put
    /// it in Topaz Video's <c>models\models</c>. Only the inner folder goes.
    /// </summary>
    [Fact]
    public async Task TakesOnlyTheInnerModelsFolderBesideAnInstalledProductsSignInRecord()
    {
        var outer = Models("Topaz Video");
        var auth = Write(Path.Combine(outer, "auth.tz"), 64);
        var description = Write(Path.Combine(outer, "prob-4.json"), 64);
        var inner = Path.Combine(outer, "models");
        Write(Path.Combine(inner, "prob-v4-fp16-ov.tz"));
        Install("Topaz Video");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([inner], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(inner));
        Assert.True(File.Exists(auth), "The sign-in record was removed.");
        Assert.True(File.Exists(description), "A model description was removed.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task KeepsAnInnerModelsFolderThatHoldsASignInRecordToo()
    {
        Write(Path.Combine(Models("Topaz Video"), "auth.tz"), 64);
        Write(Path.Combine(Models("Topaz Video"), "models", "auth.tz"), 64);
        Write(Path.Combine(Models("Topaz Video"), "models", "prob-v4-fp16-ov.tz"));
        Install("Topaz Video");

        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    public static TheoryData<string, string> InstalledEntries => new()
    {
        { "DisplayName", "Topaz Photo AI 3.1.2" },
        { "DisplayName", "Topaz Photo AI" },
        { "InstallLocation", @"D:\Apps\Topaz Photo AI\" },
    };

    /// <summary>A product installed somewhere other than Program Files is found through its <c>Uninstall</c> entry.</summary>
    [Theory]
    [MemberData(nameof(InstalledEntries))]
    public async Task AnUninstallEntryForTheProductMeansItIsInstalled(string value, string data)
    {
        CreateProduct("Topaz Photo AI");
        _registry.With(UninstallScope.Machine64, "{5B1C9E2A}_is1", (value, data));

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.Equal("Installed", step.Group);
    }

    /// <summary>The rename is why this matters: an installed Photo AI is not an installed Photo, and the reverse.</summary>
    [Theory]
    [InlineData("Topaz Photo", "Topaz Photo AI 3.1.2")]
    [InlineData("Topaz Photo AI", "Topaz Photo 1.4.0")]
    public async Task AnEntryForARenamedProductDoesNotCountForTheOther(string product, string entry)
    {
        CreateProduct(product);
        _registry.With(UninstallScope.Machine64, "{5B1C9E2A}_is1", ("DisplayName", entry));

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.Equal("No longer installed", step.Group);
    }

    /// <summary>
    /// "Not installed" is a claim, and a key Windows would not open is no evidence for it, so the
    /// product is treated as installed and keeps its sign-in record.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableUninstallKeyIsNeverReadAsNotInstalled(bool wholeScope)
    {
        var models = CreateProduct("Topaz Photo AI", withSignIn: true);

        if (wholeScope)
        {
            _registry.Refusing(UninstallScope.Machine32);
        }
        else
        {
            _registry.WithUnreadable(UninstallScope.CurrentUser, "Something");
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(models, "auth.tz"));
    }

    [Fact]
    public async Task AProgramFolderWindowsWillNotDescribeIsNeverReadAsNotInstalled()
    {
        CreateProduct("Topaz Photo AI");
        var programs = Path.Combine(_system.ProgramFiles, "Topaz Labs LLC");
        Directory.CreateDirectory(Path.Combine(programs, "Topaz Photo AI"));

        using var denied = DeniedDirectory.WithUnreadableAttributes(Path.Combine(programs, "Topaz Photo AI"));

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.Equal("Installed", step.Group);
    }

    /// <summary>
    /// A folder Topaz's model variables name is in use, whatever the uninstall keys say, however the
    /// value is written: these are set by hand.
    /// </summary>
    [Theory]
    [InlineData("TVAI_MODEL_DIR", "")]
    [InlineData("TVAI_MODEL_DATA_DIR", "")]
    [InlineData("PAI_MODEL_DIR", "")]
    [InlineData("TVAI_MODEL_DIR", "slashes")]
    [InlineData("TVAI_MODEL_DIR", "device")]
    [InlineData("TVAI_MODEL_DIR", "dots")]
    [InlineData("TVAI_MODEL_DIR", "quoted")]
    public async Task AFolderAModelVariableNamesIsInUse(string variable, string form)
    {
        var models = CreateProduct("Topaz Video AI", withSignIn: true);
        var value = form switch
        {
            "slashes" => models.Replace('\\', '/') + "/",
            "device" => @"\\?\" + models,
            "dots" => Path.Combine(models, "..", "models"),
            "quoted" => $"\"{models}\"",
            _ => models,
        };
        _environment.WithEnvironmentVariable(variable, value);

        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.3: while a Topaz application runs, an installed product's models are held back and Explore
    /// refuses them. A product that is gone has nothing loading its models, so it is still offered.
    /// </summary>
    [Fact]
    public async Task HoldsAnInstalledProductsModelsWhileTopazIsRunning()
    {
        var installed = CreateProduct("Topaz Gigapixel AI");
        var gone = CreateProduct("Topaz Sharpen AI");
        Install("Topaz Gigapixel AI");

        var provider = CreateProvider(new FakeProcessInspector("Topaz Gigapixel AI"));
        var plan = await provider.PlanAsync();

        Assert.Equal([gone], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == installed);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);

        var policy = new ExploreActionPolicy([], [.. provider.ToolRoots, .. await provider.DiscoverToolRootsAsync()], new FakeVolumeInventory());

        Assert.False(policy.MayRemove(installed).IsAllowed);
        Assert.True(policy.MayRemove(gone).IsAllowed);
    }

    /// <summary>
    /// Topaz Video renders through the ffmpeg it installs beside itself, with no window open and under
    /// a name any other program can have. A program running from a Topaz program folder holds an
    /// installed product's models back.
    /// </summary>
    [Fact]
    public async Task HoldsAnInstalledProductsModelsWhileAProgramRunsFromTopazsProgramFolder()
    {
        var installed = CreateProduct("Topaz Video AI");
        var gone = CreateProduct("Topaz Sharpen AI");
        Install("Topaz Video AI");

        var liveTrees = new FakeLiveTreeInspector().WithProgram(
            "ffmpeg", executable: Path.Combine(ProgramFolder, "Topaz Video AI", "ffmpeg.exe"));

        var plan = await CreateProvider(liveTrees: liveTrees).PlanAsync();

        Assert.Equal([gone], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == installed);
        Assert.Contains(plan.Notes, n => n.Message.Contains("ffmpeg", StringComparison.Ordinal));
    }

    /// <summary>The same question is asked again at the clean, for a render started after the preview.</summary>
    [Fact]
    public async Task ARenderStartedAfterThePreviewKeepsTheModelsAtTheClean()
    {
        var installed = CreateProduct("Topaz Video AI");
        Install("Topaz Video AI");

        var liveTrees = new FakeLiveTreeInspector();
        var provider = CreateProvider(liveTrees: liveTrees);
        var plan = await provider.PlanAsync();
        Assert.Equal([installed], plan.TargetedPaths);

        liveTrees.WithProgram("ffmpeg", executable: Path.Combine(ProgramFolder, "Topaz Video AI", "ffmpeg.exe"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(installed), "The models were removed while a render was running.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task HoldsAnInstalledProductsModelsWhereDegufferCannotTellWhatIsRunning()
    {
        CreateProduct("Topaz Video AI");
        Install("Topaz Video AI");

        var plan = await CreateProvider(liveTrees: FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task ListsProductsThatAreNoLongerInstalledFirst()
    {
        var installed = CreateProduct("Topaz Gigapixel AI");
        var gone = CreateProduct("Topaz Mask AI");
        Install("Topaz Gigapixel AI");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([gone, installed], plan.Steps.OfType<DeleteStep>().Select(s => s.Path));
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DeclinesAModelsFolderThatIsALink(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var model = Write(Path.Combine(outside, "model.tz"));
        Directory.CreateDirectory(Product("Topaz DeNoise AI"));
        DirectoryLink.Create(kind, Models("Topaz DeNoise AI"), outside);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(File.Exists(model));
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// A product folder moved onto another drive behind a link is a row that says so: the plan is never
    /// asked for a row that is not there, so its note could not be read.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task AProductFolderThatIsALinkIsARowThatSaysSo(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var model = Write(Path.Combine(outside, "models", "model.tz"));
        Directory.CreateDirectory(Root);
        DirectoryLink.Create(kind, Product("Topaz DeNoise AI"), outside);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(File.Exists(model));
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DeclinesAVendorFolderThatIsALink(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var model = Write(Path.Combine(outside, "Topaz DeNoise AI", "models", "model.tz"));
        DirectoryLink.Create(kind, Root, outside);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(File.Exists(model));
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task AProductFolderThatWillNotBeListedIsReportedRatherThanCalledEmpty()
    {
        CreateProduct("Topaz DeNoise AI");

        using var denied = new DeniedDirectory(Product("Topaz DeNoise AI"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryModelsFolderThePlanRemoves()
    {
        CreateProduct("Topaz DeNoise AI");
        Write(Path.Combine(Models("Topaz Video"), "auth.tz"), 64);
        Write(Path.Combine(Models("Topaz Video"), "models", "prob-v4-fp16-ov.tz"));
        Install("Topaz Video");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>§7.1 refuses in Explore exactly what the plan would not take, level by level.</summary>
    [Theory]
    [InlineData("", false)]                                         // the vendor's folder
    [InlineData("Topaz DeNoise AI", false)]                         // a product's folder
    [InlineData(@"Topaz DeNoise AI\models", true)]                  // a product that is gone
    [InlineData(@"Topaz DeNoise AI\Logs", false)]
    [InlineData(@"Topaz Video\models", false)]                      // holds the sign-in record
    [InlineData(@"Topaz Video\models\models", true)]                // the model data inside it
    [InlineData(@"Topaz Video AI\models", false)]                   // installed, sign-in record beside the models
    public void ExploreMayRemoveOnlyWhatThePlanWouldTake(string relative, bool allowed)
    {
        CreateProduct("Topaz DeNoise AI");
        Write(Path.Combine(Models("Topaz Video"), "auth.tz"), 64);
        Write(Path.Combine(Models("Topaz Video"), "models", "prob-v4-fp16-ov.tz"));
        Install("Topaz Video");
        CreateProduct("Topaz Video AI", withSignIn: true);
        Install("Topaz Video AI");

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Root : Path.Combine(Root, relative)).IsAllowed);
    }
}
