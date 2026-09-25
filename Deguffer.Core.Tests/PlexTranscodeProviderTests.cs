using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Plex Media Server's transcoder leftovers. What has to be shown is that only
/// <c>Transcode\Sessions</c> and <c>PhotoTranscoder</c> are reached, wherever Plex's registry settings
/// put them, that a segment a film is playing from survives, and that Plex's database, its sync
/// queue and its downloads folder are asserted rather than merely omitted.
/// </summary>
public sealed class PlexTranscodeProviderTests : IDisposable
{
    private static readonly TimeSpan Old = TimeSpan.FromHours(MediaServerTranscodeProvider.QuietHours + 12);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PlexTranscodeProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Data => Path.Combine(_environment.LocalAppData, PlexServerLayout.FolderName);

    private string Sessions => Path.Combine(Data, "Cache", "Transcode", "Sessions");

    private string PhotoTranscoder => Path.Combine(Data, "Cache", "PhotoTranscoder");

    private PlexTranscodeProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private FakeUserEnvironment WithSetting(string value, string folder) =>
        _environment.WithRegistryValue(PlexServerLayout.RegistryKey, value, folder);

    private static string Write(string file, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[4096]);
        return TempDirectory.Age(file, age);
    }

    /// <summary>A session a stream ended badly in, a day and a half ago, and Plex's own data beside it.</summary>
    private (string Segment, string[] Kept) CreateLayout()
    {
        var segment = Write(Path.Combine(Sessions, "plex-transcode-a1b2", "media-00001.ts"), Old);
        Write(Path.Combine(PhotoTranscoder, "3f", "3f0a.jpg"), Old);

        string[] kept =
        [
            Write(Path.Combine(Data, "Plug-in Support", "Databases", "com.plexapp.plugins.library.db"), Old),
            Write(Path.Combine(Data, "Metadata", "Movies", "a", "Info.xml"), Old),
            Write(Path.Combine(Data, "Media", "localhost", "0", "thumb.jpg"), Old),
            Write(Path.Combine(Data, "Cache", "Transcode", "Sync", "item-1", "media.mp4"), Old),
            Write(Path.Combine(Data, "Cache", "Unrecognised", "something.bin"), Old),
            Write(Path.Combine(Data, "Cache", "Transcode", "Unrecognised", "something.bin"), Old),
        ];

        return (segment, kept);
    }

    [Fact]
    public async Task ReportsNotPresentWhenPlexHasNeverRunHere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Plex's own folder also holds its database, so the folder being there must not read as presence:
    /// that would report the row and then plan nothing.
    /// </summary>
    [Fact]
    public async Task PlexsOwnFolderExistingIsNotPresence()
    {
        Write(Path.Combine(Data, "Plug-in Support", "Databases", "com.plexapp.plugins.library.db"), Old);

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// §5.2 and §5.6, proved by running a plan. The two recognised folders are emptied and stay standing.
    /// The sync queue beside <c>Sessions</c>, the database, the artwork and an unrecognised sibling at
    /// each level all survive, and the consequential ones are asserted by name.
    /// </summary>
    [Fact]
    public async Task EmptiesSessionsAndThePhotoTranscoderAloneAndAssertsPlexsDataSurvived()
    {
        var (segment, kept) = CreateLayout();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([Sessions, PhotoTranscoder], plan.TargetedPaths);
        Assert.True(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);

        foreach (var path in new[]
        {
            Data,
            Path.Combine(Data, "Plug-in Support", "Databases"),
            Path.Combine(Data, "Metadata"),
            Path.Combine(Data, "Media"),
            Path.Combine(Data, "Cache", "Transcode", "Sync"),
        })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(segment));
        Assert.True(Directory.Exists(Sessions), "the Sessions folder itself went.");
        Assert.True(Directory.Exists(PhotoTranscoder), "the PhotoTranscoder folder itself went.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(PhotoTranscoder));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder's files."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3. A film playing now is writing into the same folder as the dead sessions. Its segments are
    /// younger than the floor, so they stay, and the floor stays on the plan however the user's own
    /// guard is set.
    /// </summary>
    [Fact]
    public async Task ASegmentAFilmIsPlayingFromSurvivesTheRun()
    {
        var (dead, _) = CreateLayout();
        var live = Write(Path.Combine(Sessions, "plex-transcode-c3d4", "media-00042.ts"), TimeSpan.FromMinutes(2));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.Keep.IsOn);
        Assert.Equal(8192, plan.EstimatedBytes);

        await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(live), "a segment a film was playing from was deleted.");
    }

    /// <summary>
    /// <c>LocalAppDataPath</c> names the folder Plex's own folder is in, not Plex's folder itself. Read
    /// the other way, the provider would look for <c>Cache</c> one level too high and find nothing.
    /// </summary>
    [Fact]
    public async Task FollowsAMovedDataFolder()
    {
        var moved = Path.Combine(_temp.Path, "PlexData");
        var sessions = Path.Combine(moved, PlexServerLayout.FolderName, "Cache", "Transcode", "Sessions");
        Write(Path.Combine(sessions, "plex-transcode-a1b2", "media-00001.ts"), Old);
        CreateLayout();
        WithSetting(PlexServerLayout.DataFolderValue, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(sessions, plan.TargetedPaths);
        Assert.DoesNotContain(Sessions, plan.TargetedPaths);
    }

    /// <summary>
    /// A moved transcoder writes into <c>Transcode\Sessions</c> inside the folder the setting names.
    /// That folder is the user's choice, so everything else in it survives. The default folder is
    /// still offered, because Plex left its segments there until the setting changed.
    /// </summary>
    [Fact]
    public async Task EmptiesOnlyTranscodeSessionsInsideAMovedTranscoderFolder()
    {
        CreateLayout();
        var moved = Path.Combine(_temp.Path, "PlexTemp");
        var movedSessions = Path.Combine(moved, "Transcode", "Sessions");
        var segment = Write(Path.Combine(movedSessions, "plex-transcode-e5f6", "media-00001.ts"), Old);
        var beside = Write(Path.Combine(moved, "notes.txt"), Old);
        var besideSessions = Write(Path.Combine(moved, "Transcode", "Sync", "item.mp4"), Old);
        WithSetting(PlexServerLayout.TranscoderValue, moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Sessions, PhotoTranscoder, movedSessions], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(segment));
        Assert.True(File.Exists(beside), "a file beside the moved transcoder folder went.");
        Assert.True(File.Exists(besideSessions), "something beside Sessions in the moved folder went.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The downloads folder is not a cache. Where the transcoder's folder overlaps it, neither setting
    /// says what Plex keeps where, so the transcoder's folder is withheld and the downloads survive.
    /// </summary>
    [Fact]
    public async Task WithholdsATranscoderFolderThatOverlapsTheDownloadsFolder()
    {
        var shared = Path.Combine(_temp.Path, "PlexShared");
        var sessions = Path.Combine(shared, "Transcode", "Sessions");
        var download = Write(Path.Combine(sessions, "download-1", "film.mp4"), Old);
        WithSetting(PlexServerLayout.TranscoderValue, shared);
        WithSetting(PlexServerLayout.DownloadsValue, shared);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("prepares downloads", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(shared, StringComparison.OrdinalIgnoreCase));

        await CreateProvider().ExecuteAsync(plan);

        Assert.True(File.Exists(download), "a download waiting to go to a phone was deleted.");
    }

    /// <summary>
    /// A downloads setting that names no full path could be anywhere, so no transcoder folder can be
    /// shown not to overlap it. The photo transcoder's folder is not the transcoder's, and stays offered.
    /// </summary>
    [Fact]
    public async Task WithholdsEveryTranscoderFolderWhenTheDownloadsFolderCannotBePlaced()
    {
        CreateLayout();
        WithSetting(PlexServerLayout.DownloadsValue, @"Downloads\Plex");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([PhotoTranscoder], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains(@"'Downloads\Plex'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Sessions, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A declared path reached by name has none of the protection an enumeration gives.</summary>
    [Fact]
    public async Task AJunctionedSessionsFolderIsNamedRatherThanFollowed()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var irreplaceable = Write(Path.Combine(outside, "irreplaceable.mkv"), Old);
        Directory.CreateDirectory(Path.GetDirectoryName(Sessions)!);
        SymbolicLink.ToDirectory(Sessions, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(irreplaceable), "a junctioned Sessions folder was deleted through.");
    }

    /// <summary>§5.3, named by the transcoder's own process.</summary>
    [Fact]
    public async Task WarnsWhileTheTranscoderIsRunning()
    {
        CreateLayout();

        var plan = await CreateProvider(new FakeProcessInspector("Plex Transcoder")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Plex Transcoder", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1 removes a folder whole and applies no floor, so Explore may take nothing from Plex's folder
    /// or from the folders its settings name. The Storage page is the route that can spare a live stream.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Cache")]
    [InlineData(@"Cache\Transcode\Sessions")]
    [InlineData(@"Cache\PhotoTranscoder")]
    [InlineData("Metadata")]
    public void ExploreRemovesNothingFromPlexsFolder(string relative)
    {
        CreateLayout();

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(relative.Length == 0 ? Data : Path.Combine(Data, relative)).IsAllowed);
    }

    [Fact]
    public void ExploreRemovesNothingFromTheFoldersPlexsSettingsName()
    {
        var moved = Path.Combine(_temp.Path, "PlexTemp");
        var downloads = Path.Combine(_temp.Path, "PlexDownloads");
        Write(Path.Combine(moved, "Transcode", "Sessions", "s", "a.ts"), Old);
        Write(Path.Combine(downloads, "d", "film.mp4"), Old);
        WithSetting(PlexServerLayout.TranscoderValue, moved);
        WithSetting(PlexServerLayout.DownloadsValue, downloads);

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(Path.Combine(moved, "Transcode", "Sessions")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(downloads, "d")).IsAllowed);
    }

    /// <summary>The registry is read once per planning pass, and again after the planner invalidates.</summary>
    [Fact]
    public async Task ReadsPlexsSettingsOncePerPass()
    {
        CreateLayout();
        var provider = CreateProvider();

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        _ = provider.ToolRoots;
        var reads = _environment.RegistryReads;

        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(3, reads);
        Assert.Equal(6, _environment.RegistryReads);
    }
}
