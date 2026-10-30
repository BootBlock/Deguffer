using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The system folders of replaced IntelliJ-platform IDE versions. The layout is the one measured on a
/// workstation with one replaced Android Studio: two version folders under
/// <c>%LOCALAPPDATA%\Google</c>, both with a <c>.home</c> naming the same install, whose
/// <c>product-info.json</c> names the newer one. Every timestamp is set, because the rule is an age.
/// </summary>
public sealed class JetBrainsSupersededSystemProviderTests : IDisposable
{
    private const string Old = "AndroidStudio2025.2.3";
    private const string Current = "AndroidStudio2025.3.1";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ManualTimeProvider _clock = new();
    private readonly string _install;

    public JetBrainsSupersededSystemProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _install = Path.Combine(_temp.Path, "Program Files", "Android", "Android Studio");
        Write(Path.Combine(_install, "product-info.json"), $"{{\"name\": \"Android Studio\", \"dataDirectoryName\": \"{Current}\"}}");
    }

    public void Dispose() => _temp.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private string Google => Path.Combine(_environment.LocalAppData, "Google");

    private string JetBrains => Path.Combine(_environment.LocalAppData, "JetBrains");

    /// <summary>
    /// The measured case, with §5.6 asserted against the disk: the replaced version's caches, indexes
    /// and temporary files go. Its own folder, its local history, its logs, its <c>.home</c>, a child
    /// nothing recognises, its configuration folder with the credential store, the installed version,
    /// the vendor folder and Google's other folders all survive.
    /// </summary>
    [Fact]
    public async Task OffersAReplacedVersionsDerivedFoldersUnusedFor180DaysAndNothingElse()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(250));
        var current = CreateVersion(Google, Current, home: _install, age: TimeSpan.FromDays(3));
        var configuration = Path.Combine(_environment.RoamingAppData, "Google", Old);
        Write(Path.Combine(configuration, "c.kdbx"), "credentials");
        var drive = Path.Combine(Google, "DriveFS");
        Write(Path.Combine(drive, "settings.json"), "{}");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Derived(old), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == old && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(old, "LocalHistory") && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(old, "log") && p.PresenceBefore is PathPresence.Present);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == configuration && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == current && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Google && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == drive && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(Derived(old), child => Assert.False(LongPath.DirectoryExists(child)));
        Assert.True(LongPath.FileExists(Path.Combine(old, "LocalHistory", "changes.storageData")));
        Assert.True(LongPath.FileExists(Path.Combine(old, "log", "idea.log")));
        Assert.True(LongPath.FileExists(Path.Combine(old, ".home")));
        Assert.True(LongPath.FileExists(Path.Combine(old, "plugins", "action.script")));
        Assert.True(LongPath.FileExists(Path.Combine(configuration, "c.kdbx")));
        Assert.True(LongPath.DirectoryExists(current));
        Assert.True(LongPath.FileExists(Path.Combine(drive, "settings.json")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>A version whose install folder is gone is not installed, by the IDE's own test.</summary>
    [Fact]
    public async Task OffersAVersionWhoseInstallIsGone()
    {
        var old = CreateVersion(JetBrains, "Rider2024.2", home: Path.Combine(_temp.Path, "Gone", "Rider"), age: TimeSpan.FromDays(400));

        Assert.Equal(Derived(old), (await CreateProvider().PlanAsync()).TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverOffersTheInstalledVersionHoweverOld()
    {
        var current = CreateVersion(Google, Current, home: _install, age: TimeSpan.FromDays(400));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == current);
    }

    /// <summary>
    /// The IDE counts a missing <c>product-info.json</c> as installed, allowing for a self-built
    /// install, and so does this.
    /// </summary>
    [Fact]
    public async Task AnInstallWithoutProductInfoCountsAsInstalled()
    {
        var self = Path.Combine(_temp.Path, "SelfBuilt");
        Directory.CreateDirectory(self);
        CreateVersion(JetBrains, "IdeaIC2023.1", home: self, age: TimeSpan.FromDays(400));

        Assert.Empty((await CreateProvider().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// The shelf life, from either side. The newest entry decides, a folder's own time included, as the
    /// IDE counts directories as well as files.
    /// </summary>
    [Theory]
    [InlineData("file", 179, false)]
    [InlineData("file", 181, true)]
    [InlineData("folder", 10, false)]
    public async Task OffersAReplacedVersionOnlyOnceNothingInItChangedFor180Days(string newest, int daysAgo, bool offered)
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));
        var deep = Path.Combine(old, "caches", "deep");

        if (newest == "file")
        {
            File.SetLastWriteTimeUtc(Path.Combine(deep, "index.dat"), Now - TimeSpan.FromDays(daysAgo));
        }
        else
        {
            Directory.SetLastWriteTimeUtc(deep, Now - TimeSpan.FromDays(daysAgo));
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(offered, plan.TargetedPaths.Contains(Path.Combine(old, "caches")));

        if (!offered)
        {
            Assert.Contains(plan.Notes, n => n.Message.Contains("within the last 180 days", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The IDE skips what it cannot read and keeps the newest time of the rest, which reads older than
    /// the truth. A folder Windows would not list leaves the age unknown, and the version is kept.
    /// </summary>
    [Fact]
    public async Task KeepsAVersionPartOfWhichCouldNotBeRead()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));
        var deep = Path.Combine(old, "caches", "deep");

        CleanupPlan plan;

        using (new DeniedDirectory(deep))
        {
            plan = await CreateProvider().PlanAsync();
        }

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == old);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every way the installed test cannot be read keeps the version, where the IDE's own dialog would
    /// call it not installed.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("noDataDirectory")]
    public async Task KeepsAVersionWhoseInstalledTestCannotBeRead(string how)
    {
        var home = how switch
        {
            "relative" => "Android Studio",
            "noDataDirectory" => Path.Combine(_temp.Path, "Odd"),
            _ => null,
        };

        if (how == "noDataDirectory")
        {
            Write(Path.Combine(home!, "product-info.json"), "{\"name\": \"Android Studio\"}");
        }

        var old = CreateVersion(Google, Old, home, age: TimeSpan.FromDays(400));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == old);
        Assert.Contains(plan.Notes, n => n.Message.Contains("still installed could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// Google's folder holds other products, so only Android Studio's names are read there, however
    /// closely another folder follows the platform's layout.
    /// </summary>
    [Fact]
    public async Task ReadsOnlyAndroidStudioUnderGoogle()
    {
        var other = CreateVersion(Google, "Earth7.3", home: Path.Combine(_temp.Path, "Gone"), age: TimeSpan.FromDays(400));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == other);
    }

    /// <summary>
    /// A version started between the preview and the clean rewrites its <c>.home</c>, and its folder
    /// is held back at the clean.
    /// </summary>
    [Fact]
    public async Task HoldsBackAVersionStartedSinceThePreview()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        Assert.Equal(Derived(old), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        File.SetLastWriteTimeUtc(Path.Combine(old, ".home"), Now);
        await provider.ExecuteAsync(plan);

        Assert.All(Derived(old), child => Assert.True(LongPath.DirectoryExists(child)));
    }

    /// <summary>
    /// At the clean, a <c>.home</c> Windows will not describe is a question that could not be asked,
    /// and holds the version back rather than reading as "not started".
    /// </summary>
    [Fact]
    public async Task HoldsBackAVersionWhoseHomeFileCannotBeAskedAboutAtTheClean()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        Assert.Equal(Derived(old), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        using (DeniedDirectory.WithUnreadableFile(Path.Combine(old, ".home")))
        {
            await provider.ExecuteAsync(plan);
        }

        Assert.All(Derived(old), child => Assert.True(LongPath.DirectoryExists(child)));
    }

    /// <summary>
    /// A <c>.home</c> Windows would not let Deguffer read is no evidence that the version is replaced,
    /// where the IDE's dialog would call it not installed.
    /// </summary>
    [Fact]
    public async Task KeepsAVersionWhoseHomeFileIsRefused()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));

        CleanupPlan plan;

        using (DeniedDirectory.WithUnreadableFile(Path.Combine(old, ".home")))
        {
            plan = await CreateProvider().PlanAsync();
        }

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == old);

        // Kept by the installed test, not by the age walk the refusal on the folder would also stop.
        Assert.Contains(plan.Notes, n => n.Message.Contains("still installed could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("how recently", StringComparison.Ordinal));
    }

    /// <summary>A version folder that is a link is never looked through, and never a target.</summary>
    [Fact]
    public async Task NeverLooksThroughAVersionFolderThatIsALink()
    {
        var elsewhere = CreateVersion(Path.Combine(_temp.Path, "Elsewhere"), Old, home: _install, age: TimeSpan.FromDays(400));
        Directory.CreateDirectory(Google);
        var link = Path.Combine(Google, Old);
        Junction.ToDirectory(link, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1 reads the same rule: in a version the plan offers, Explore allows the derived folders and
    /// refuses its local history, logs and anything unrecognised. In the installed version it refuses
    /// everything.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesWhatThePlanLeavesInEachVersionFolder()
    {
        var old = CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));
        var current = CreateVersion(Google, Current, home: _install, age: TimeSpan.FromDays(3));

        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.True(policy.MayRemove(Path.Combine(old, "caches")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(old, "LocalHistory")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(old, "log")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(old, "plugins")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(current, "caches")).IsAllowed);
    }

    [Fact]
    public async Task IsAbsentWhereNoVendorFolderExists()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryFolderThePlanRemoves()
    {
        CreateVersion(Google, Old, home: _install, age: TimeSpan.FromDays(400));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Fact]
    public void IsShipped()
    {
        using var shipped = new ShippedProviders();

        Assert.Single(shipped.Providers, p => p is JetBrainsSupersededSystemProvider);
    }

    /// <summary>The derived folders of <paramref name="version"/>, in order.</summary>
    private static IEnumerable<string> Derived(string version) =>
        JetBrainsSupersededSystemProvider.DerivedChildren.Select(child => Path.Combine(version, child)).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A version's system folder as the measured one is laid out, with a <c>.home</c> naming
    /// <paramref name="home"/> (none where it is null), and every entry last written
    /// <paramref name="age"/> ago.
    /// </summary>
    private string CreateVersion(string vendor, string name, string? home, TimeSpan age)
    {
        var folder = Path.Combine(vendor, name);
        var deep = Path.Combine(folder, "caches", "deep");

        if (home is not null)
        {
            Write(Path.Combine(folder, ".home"), home);
        }

        Write(Path.Combine(deep, "index.dat"), new string('x', 4096));
        Write(Path.Combine(folder, "index", "stubs", "stubs.dat"), new string('x', 4096));
        Write(Path.Combine(folder, "tmp", "scratch.tmp"), "temporary");
        Write(Path.Combine(folder, "LocalHistory", "changes.storageData"), "history");
        Write(Path.Combine(folder, "plugins", "action.script"), "pending");
        Write(Path.Combine(folder, "log", "idea.log"), "started");

        var then = Now - age;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, then);
        }

        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            Directory.SetLastWriteTimeUtc(directory, then);
        }

        return folder;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private JetBrainsSupersededSystemProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner(), _clock);
}
