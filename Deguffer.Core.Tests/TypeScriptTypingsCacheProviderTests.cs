using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// TypeScript keeps one folder of downloaded typings per version, and its installer runs npm in the
/// folder of the version that is working. So a version folder is offered whole, and held back while a
/// program works in it, which is the only sign of use there is: nothing runs under the name
/// <c>tsserver</c>.
/// </summary>
public sealed class TypeScriptTypingsCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public TypeScriptTypingsCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private TypeScriptTypingsCacheProvider CreateProvider(FakeLiveTreeInspector? liveTrees = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    private string Root => Path.Combine(_environment.LocalAppData, "Microsoft", "TypeScript");

    /// <summary>A version folder as the installer leaves it: an npm project of downloaded declarations.</summary>
    private string CreateVersion(string name)
    {
        var folder = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(folder, "node_modules", "@types", "node"));
        File.WriteAllText(Path.Combine(folder, "package.json"), "{ \"private\": true }");
        File.WriteAllBytes(Path.Combine(folder, "node_modules", "@types", "node", "index.d.ts"), new byte[4096]);
        return folder;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTypeScriptHasNeverDownloadedTypings()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Every folder named by a major and minor version is offered, under that version, and anything
    /// else in the root is asserted to survive (§5.6), as is the vendor folder above it.
    /// </summary>
    [Fact]
    public async Task OffersEachVersionFolderAndNothingElse()
    {
        var older = CreateVersion("4.8");
        var current = CreateVersion("6.0");
        var prerelease = CreateVersion("6.0-dev");
        var patch = CreateVersion("5.9.3");
        var bystander = CreateVersion("node_modules");
        var vendorFile = Path.Combine(_environment.LocalAppData, "Microsoft", "settings.dat");
        File.WriteAllText(vendorFile, "keep");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { older, current }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Steps.OfType<DeleteDirectoryStep>(), s => s.Identity == new ItemIdentity("6.0", "TypeScript 6.0"));

        foreach (var survivor in new[] { Root, prerelease, patch, bystander })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(current));
        Assert.True(Directory.Exists(prerelease));
        Assert.True(Directory.Exists(patch));
        Assert.True(Directory.Exists(bystander));
        Assert.True(File.Exists(vendorFile));
    }

    /// <summary>
    /// A version an install is working in is held back and asserted standing, and the versions beside
    /// it are still offered: one working is using none of the others.
    /// </summary>
    [Fact]
    public async Task HoldsBackAVersionAProgramIsWorkingIn()
    {
        var idle = CreateVersion("5.8");
        var working = CreateVersion("6.0");

        var plan = await CreateProvider(new FakeLiveTreeInspector(working)).PlanAsync();

        Assert.Equal([idle], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(working, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("TypeScript 6.0's typings", StringComparison.Ordinal));
    }

    /// <summary>"Could not tell" is not "not in use": where the process table could not be read, nothing is offered.</summary>
    [Fact]
    public async Task OffersNothingWhereItCannotTellWhetherAVersionIsInUse()
    {
        var version = CreateVersion("6.0");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(version, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheToolRootRecognisesOnlyAVersionFolder()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Root, root.Path);
        Assert.True(root.Recognises(new ToolRootChild("6.0", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("6.0", ChildKind.File)));
        Assert.False(root.Recognises(new ToolRootChild("6.0-dev", ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("6", ChildKind.Folder)));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateVersion("6.0");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>A plan holds display paths; the I/O beneath it extends them where it acts (§6.3).</summary>
    [Fact]
    public async Task ThePlanHoldsDisplayPaths()
    {
        CreateVersion("6.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);
        Assert.All(plan.TargetedPaths, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));
    }
}
