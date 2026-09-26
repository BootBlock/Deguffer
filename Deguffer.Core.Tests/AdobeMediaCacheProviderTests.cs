using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The media cache Adobe's video and audio applications share.
///
/// <para>Four things have to hold. Only the contents of Adobe's cache folders are ever reached, and
/// <c>%APPDATA%\Adobe\Common</c>, the user's LUTs, templates and Team Projects' auto-saves beside them,
/// and anything unrecognised there survive a run and are asserted by name. A cache the registry moved
/// is followed into the folder the setting names, and nothing else in that folder, which is the user's
/// own, is reached. A moved cache that is not on a disk in this computer is left alone and said so.
/// And nothing is offered or removed while an Adobe application runs.</para>
/// </summary>
public sealed class AdobeMediaCacheProviderTests : IDisposable
{
    private const string Release = @"Software\Adobe\Common 13.0\Media Cache";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public AdobeMediaCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _volumes.With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Common => Path.Combine(_environment.RoamingAppData, "Adobe", "Common");

    private string Files => Path.Combine(Common, AdobeMediaCacheLayout.FilesFolder);

    private string Peaks => Path.Combine(Common, AdobeMediaCacheLayout.PeakFolder);

    private string Database => Path.Combine(Common, AdobeMediaCacheLayout.DatabaseFolder);

    private AdobeMediaCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, volumes: _volumes);

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    /// <summary>A cache in the default folder, and what Adobe and the user keep beside it.</summary>
    private (string[] Cached, string[] Kept) CreateLayout()
    {
        string[] cached =
        [
            Write(Path.Combine(Files, "interview.wav 48000.cfa")),
            Write(Path.Combine(Files, "interview.wav 48000.pek")),
            Write(Path.Combine(Files, "A001_C002.mov 1234.ims")),
            Write(Path.Combine(Peaks, "music.mp3 44100.pek")),
            Write(Path.Combine(Database, "Media Cache Database.db")),
        ];

        string[] kept =
        [
            Write(Path.Combine(Common, "LUTs", "Input", "graded.cube")),
            Write(Path.Combine(Common, "Motion Graphics Templates", "lower-third.mogrt")),
            Write(Path.Combine(Common, "Team Projects Local Hub", "a1b2c3", "autosave.db")),
            Write(Path.Combine(Common, "Unrecognised", "something.bin")),
            Write(Path.Combine(Common, "identity.dat")),
        ];

        return (cached, kept);
    }

    [Fact]
    public async Task ReportsNotPresentWhereAdobeHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Adobe's shared folder also holds the user's LUTs and templates, so the folder being there must not
    /// read as presence: that would report the row and then plan nothing.
    /// </summary>
    [Fact]
    public async Task AdobesSharedFolderAloneIsNotPresence()
    {
        Write(Path.Combine(Common, "LUTs", "Input", "graded.cube"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// §5.2 and §5.6, proved by running a plan. The three cache folders are emptied and stay standing,
    /// and everything beside them, known or not, survives, the user's data asserted by name.
    /// </summary>
    [Fact]
    public async Task EmptiesTheCacheFoldersAloneAndAssertsWhatIsBesideThemSurvived()
    {
        var (cached, kept) = CreateLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal([Files, Peaks, Database], plan.TargetedPaths);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);

        foreach (var path in new[]
        {
            Common,
            Path.Combine(Common, "LUTs"),
            Path.Combine(Common, "Motion Graphics Templates"),
            Path.Combine(Common, "Team Projects Local Hub"),
        })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(cached, path => Assert.False(File.Exists(path), $"{path} was left behind."));
        Assert.All([Files, Peaks, Database], folder => Assert.True(Directory.Exists(folder), $"{folder} itself went."));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the media cache."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Adobe's values name the folder it writes its own folder into, with a trailing separator, as its
    /// default does. Only Adobe's folder inside is emptied, the user's footage beside it survives, and
    /// the default folder is still examined, because Adobe leaves part of a moved cache there.
    /// </summary>
    [Fact]
    public async Task FollowsAMovedCacheIntoTheFolderAdobeKeepsInsideTheOneTheSettingNames()
    {
        var (_, kept) = CreateLayout();
        var chosen = Path.Combine(_temp.Path, "scratch-disk", "Adobe");
        var database = Path.Combine(_temp.Path, "database-disk");

        _environment
            .WithRegistryValue(Release, AdobeMediaCacheLayout.FilesValue, chosen + @"\")
            .WithRegistryValue(Release, AdobeMediaCacheLayout.DatabaseValue, database + @"\");

        var movedFile = Write(Path.Combine(chosen, AdobeMediaCacheLayout.FilesFolder, "interview.wav 48000.cfa"));
        var movedDatabase = Write(Path.Combine(database, AdobeMediaCacheLayout.DatabaseFolder, "Media Cache Database.db"));
        var footage = Write(Path.Combine(chosen, "Footage", "A001_C002.mov"));
        var besideTheDatabase = Write(Path.Combine(database, "Exports", "final.mp4"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [
                Files, Peaks, Database,
                Path.Combine(chosen, AdobeMediaCacheLayout.FilesFolder),
                Path.Combine(database, AdobeMediaCacheLayout.DatabaseFolder),
            ],
            plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(database, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(movedFile));
        Assert.False(File.Exists(movedDatabase));
        Assert.True(File.Exists(footage), "footage beside the moved cache went with it.");
        Assert.True(File.Exists(besideTheDatabase), "a file beside the moved database went with it.");
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the media cache."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Every release's key is read, whatever its version, and only a release's key: another Adobe
    /// application's key with the same value names is not Adobe's shared cache. Two releases naming one
    /// folder declare it once.
    /// </summary>
    [Fact]
    public async Task ReadsEveryReleasesKeyAndNothingElse()
    {
        var older = Path.Combine(_temp.Path, "older");
        var newer = Path.Combine(_temp.Path, "newer");
        var other = Path.Combine(_temp.Path, "other");

        _environment
            .WithRegistryValue(@"Software\Adobe\Common 12.0\Media Cache", AdobeMediaCacheLayout.FilesValue, older)
            .WithRegistryValue(@"Software\Adobe\Common 25.0\Media Cache", AdobeMediaCacheLayout.FilesValue, newer)
            .WithRegistryValue(@"Software\Adobe\Common 26.0\Media Cache", AdobeMediaCacheLayout.FilesValue, newer)
            .WithRegistryValue(@"Software\Adobe\Photoshop\Media Cache", AdobeMediaCacheLayout.FilesValue, other);

        Write(Path.Combine(older, AdobeMediaCacheLayout.FilesFolder, "a.cfa"));
        Write(Path.Combine(newer, AdobeMediaCacheLayout.FilesFolder, "b.cfa"));
        var unrelated = Write(Path.Combine(other, AdobeMediaCacheLayout.FilesFolder, "c.cfa"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [
                Path.Combine(older, AdobeMediaCacheLayout.FilesFolder),
                Path.Combine(newer, AdobeMediaCacheLayout.FilesFolder),
            ],
            plan.TargetedPaths);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(unrelated));
    }

    /// <summary>
    /// A cache on a share, or on a drive that is not in this computer, may be the one an Adobe application
    /// on another computer is using now, which no process table here can show. It is named, not taken,
    /// and the row does not read as clear.
    /// </summary>
    [Fact]
    public async Task LeavesAMovedCacheThatIsNotOnALocalDiskAlone()
    {
        var share = Path.Combine(_temp.Path, "share");
        _volumes.Without(_temp.Path).With(_environment.UserProfile);
        _environment.WithRegistryValue(Release, AdobeMediaCacheLayout.FilesValue, share);
        var shared = Write(Path.Combine(share, AdobeMediaCacheLayout.FilesFolder, "a.cfa"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(Path.Combine(share, AdobeMediaCacheLayout.FilesFolder), StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(shared));
    }

    /// <summary>A cloud mount that says its files are elsewhere is not a local disk either.</summary>
    [Fact]
    public async Task LeavesAMovedCacheOnACloudMountAlone()
    {
        var cloud = Path.Combine(_temp.Path, "cloud");
        _volumes.With(cloud, features: VolumeFeatures.RemoteStorage);
        _environment.WithRegistryValue(Release, AdobeMediaCacheLayout.FilesValue, cloud);
        Write(Path.Combine(cloud, AdobeMediaCacheLayout.FilesFolder, "a.cfa"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// A roaming profile can put Adobe's default folder on a share. It is left alone where it is there,
    /// and a machine with no Adobe on such a profile is told nothing.
    /// </summary>
    [Fact]
    public async Task LeavesADefaultFolderOnAShareAloneAndSaysNothingWhereThereIsNone()
    {
        _volumes.Without(_temp.Path);

        Assert.False(await CreateProvider().IsPresentAsync());

        var (cached, _) = CreateLayout();
        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(Files, StringComparison.Ordinal));
        Assert.All(cached, path => Assert.True(File.Exists(path)));
    }

    /// <summary>
    /// A setting that is not a full path could name any folder, so it is said out loud and nothing is
    /// resolved against Deguffer's own working directory.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenASettingIsNotAFullPath()
    {
        _environment.WithRegistryValue(Release, AdobeMediaCacheLayout.FilesValue, @"Adobe\Cache");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(@"'Adobe\Cache'", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.3: Adobe says to close the application first, and its database is open while it runs, so
    /// nothing is offered, each cache folder is named, the row does not read as clear, and Explore
    /// refuses everything in and beside it.
    /// </summary>
    [Theory]
    [InlineData("Adobe Premiere Pro")]
    [InlineData("AfterFX")]
    [InlineData("Adobe Audition")]
    [InlineData("Adobe Media Encoder")]
    [InlineData("dynamiclinkmanager")]
    public async Task NothingIsOfferedWhileAnAdobeApplicationRuns(string process)
    {
        var (cached, _) = CreateLayout();

        var provider = CreateProvider(new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Files, StringComparison.OrdinalIgnoreCase));

        var roots = await provider.DiscoverToolRootsAsync();

        Assert.False(Assert.Single(roots, r => r.Path.Equals(Common, StringComparison.OrdinalIgnoreCase))
            .Recognises(AdobeMediaCacheLayout.FilesFolder));
        Assert.False(Assert.Single(roots, r => r.Path.Equals(Files, StringComparison.OrdinalIgnoreCase))
            .Recognises("interview.wav 48000.cfa"));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(cached, path => Assert.True(File.Exists(path)));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>The clean asks again, so Premiere Pro opened while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task AnApplicationStartedAfterThePreviewHoldsTheCacheBack()
    {
        var (cached, _) = CreateLayout();

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([Files, Peaks, Database], plan.TargetedPaths);

        inspector.WithRunning("Adobe Premiere Pro");
        var result = await provider.ExecuteAsync(plan);

        Assert.All(cached, path => Assert.True(File.Exists(path), $"{path} was removed under a Premiere Pro started after the preview."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A cache folder that is a link is declined and named, and nothing it points at is touched.</summary>
    [Fact]
    public async Task ACacheFolderThatIsALinkIsLeftAlone()
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var target = Write(Path.Combine(elsewhere, "a.cfa"));
        Directory.CreateDirectory(Common);
        SymbolicLink.ToDirectory(Files, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Files, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(target));
    }

    /// <summary>
    /// §7.1: Explore may take from Adobe's shared folder only the cache folders, and nothing beside them,
    /// whether or not Deguffer knows what it is.
    /// </summary>
    [Theory]
    [InlineData(AdobeMediaCacheLayout.FilesFolder, true)]
    [InlineData(AdobeMediaCacheLayout.PeakFolder, true)]
    [InlineData(AdobeMediaCacheLayout.DatabaseFolder, true)]
    [InlineData("LUTs", false)]
    [InlineData("Motion Graphics Templates", false)]
    [InlineData("Team Projects Local Hub", false)]
    [InlineData("Unrecognised", false)]
    public async Task ExploreMayTakeOnlyTheCacheFoldersFromAdobesSharedFolder(string child, bool recognised)
    {
        CreateLayout();

        var root = Assert.Single(
            await CreateProvider().DiscoverToolRootsAsync(),
            r => r.Path.Equals(Common, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(recognised, root.Recognises(child));
    }

    /// <summary>G4: presence, the plan and Explore read Adobe's settings once between them.</summary>
    [Fact]
    public async Task ReadsAdobesSettingsOncePerPass()
    {
        _environment.WithRegistryValue(Release, AdobeMediaCacheLayout.FilesValue, Path.Combine(_temp.Path, "moved"));
        CreateLayout();

        var provider = CreateProvider();

        await provider.IsPresentAsync();
        var reads = _environment.RegistryReads;

        await provider.PlanAsync();
        await provider.DiscoverToolRootsAsync();

        Assert.Equal(reads, _environment.RegistryReads);

        provider.InvalidateCaches();
        await provider.IsPresentAsync();

        Assert.True(_environment.RegistryReads > reads, "a rescan reused settings read before it.");
    }
}
