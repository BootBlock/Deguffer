using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// Jellyfin's transcoder leftovers. A moved transcoder folder is used exactly as it is named, so the
/// evidence that a folder is Jellyfin's is the marker file Jellyfin writes into it, and what has to be
/// shown is that a folder with no evidence is never emptied.
/// </summary>
public sealed class JellyfinTranscodeProviderTests : IDisposable
{
    private static readonly TimeSpan Old = TimeSpan.FromHours(MediaServerTranscodeProvider.QuietHours + 12);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public JellyfinTranscodeProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The data folder the installer records, which is the user's choice.</summary>
    private string Data => Path.Combine(_temp.Path, "JellyfinData");

    private string Transcodes => Path.Combine(Data, "cache", "transcodes");

    private JellyfinTranscodeProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private void Record(string value, string text, RegistryView view = RegistryView.Registry32) =>
        _environment.WithMachineRegistryValue(JellyfinServerLayout.RegistryKey, value, text, view);

    private static string Write(string file, TimeSpan age, string text = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.Length > 0 ? text : new string('x', 4096));
        return TempDirectory.Age(file, age);
    }

    private static string Settings(string element, string value) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <EncodingOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <{element}>{value}</{element}>
        </EncodingOptions>
        """;

    /// <summary>A transcoder folder carrying Jellyfin's marker, holding one dead segment.</summary>
    private static string Transcoding(string folder)
    {
        Write(Path.Combine(folder, JellyfinServerLayout.Marker), Old, "transcode");
        return Write(Path.Combine(folder, "0b1c2d.ts"), Old);
    }

    /// <summary>The data folder's own contents, which must all survive.</summary>
    private string[] CreateData(string data) =>
    [
        Write(Path.Combine(data, "config", "system.xml"), Old, "<ServerConfiguration />"),
        Write(Path.Combine(data, "data", "jellyfin.db"), Old),
        Write(Path.Combine(data, "data", "backups", "backup.zip"), Old),
        Write(Path.Combine(data, "metadata", "library", "poster.jpg"), Old),
        Write(Path.Combine(data, "plugins", "configurations", "plugin.xml"), Old),
        Write(Path.Combine(data, "root", "default", "Movies", "options.xml"), Old),
        Write(Path.Combine(data, "cache", "images", "resized.webp"), Old),
    ];

    [Fact]
    public async Task ReportsNotPresentWhenJellyfinIsNotInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The installer writes its key in the 32-bit view, because it is a 32-bit program. A 64-bit
    /// process that asked the ordinary view would find nothing and report Jellyfin as not installed.
    /// </summary>
    [Fact]
    public async Task ReadsTheDataFolderFromTheInstallersThirtyTwoBitKey()
    {
        CreateData(Data);
        Transcoding(Transcodes);
        Record(JellyfinServerLayout.DataFolderValue, Data);

        Assert.Equal([Transcodes], (await CreateProvider().PlanAsync()).TargetedPaths);

        var wrongView = new FakeUserEnvironment(Path.Combine(_temp.Path, "other"))
            .WithMachineRegistryValue(JellyfinServerLayout.RegistryKey, JellyfinServerLayout.DataFolderValue, Data, RegistryView.Registry64);
        var provider = new JellyfinTranscodeProvider(
            wrongView, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

        Assert.False(await provider.IsPresentAsync());
    }

    /// <summary>
    /// §5.2 and §5.6, proved by running a plan. The transcoder folder is emptied and stays, and the
    /// database, the backups, the settings and the rest of the cache all survive and are asserted.
    /// </summary>
    [Fact]
    public async Task EmptiesTheTranscoderFolderAloneAndAssertsJellyfinsDataSurvived()
    {
        var kept = CreateData(Data);
        var segment = Transcoding(Transcodes);
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([Transcodes], plan.TargetedPaths);

        foreach (var name in new[] { "", "config", "data", @"data\backups", "metadata", "plugins", "root" })
        {
            var path = name.Length == 0 ? Data : Path.Combine(Data, name);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(segment));
        Assert.True(Directory.Exists(Transcodes), "the transcoder folder itself went.");
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder's files."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.3. A segment written two minutes ago belongs to a film playing now.</summary>
    [Fact]
    public async Task ASegmentAFilmIsPlayingFromSurvivesTheRun()
    {
        CreateData(Data);
        var dead = Transcoding(Transcodes);
        var live = Write(Path.Combine(Transcodes, "9f8e7d.ts"), TimeSpan.FromMinutes(2));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(live), "a segment a film was playing from was deleted.");
    }

    /// <summary>
    /// A moved transcoder folder is written into as it is named, so the folder has to prove it is
    /// Jellyfin's. With the marker, its contents go and everything beside it stays.
    /// </summary>
    [Fact]
    public async Task EmptiesAMovedTranscoderFolderThatCarriesJellyfinsMarker()
    {
        CreateData(Data);
        var moved = Path.Combine(_temp.Path, "Scratch", "JellyfinTranscodes");
        var segment = Transcoding(moved);
        var beside = Write(Path.Combine(_temp.Path, "Scratch", "notes.txt"), Old);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", moved));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(segment));
        Assert.True(File.Exists(beside), "a file beside the moved transcoder folder went.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The unrecognised case. A folder the settings name, but which Jellyfin never marked, may be
    /// anything the user pointed at: it is named, asserted, and left whole.
    /// </summary>
    [Fact]
    public async Task NeverEmptiesAFolderWithoutJellyfinsMarker()
    {
        CreateData(Data);
        var moved = Path.Combine(_temp.Path, "Films");
        var film = Write(Path.Combine(moved, "holiday.mkv"), Old);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", moved));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(JellyfinServerLayout.Marker, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(film), "a folder Jellyfin never marked was emptied.");
    }

    /// <summary>
    /// A server older than the marker is recognised by the <c>CACHEDIR.TAG</c> it writes in its cache
    /// folder, and only at the default folder, whose name is Jellyfin's own.
    /// </summary>
    [Fact]
    public async Task AcceptsTheDefaultFolderUnderACacheJellyfinTagged()
    {
        CreateData(Data);
        var segment = Write(Path.Combine(Transcodes, "0b1c2d.ts"), Old);
        Write(Path.Combine(Data, "cache", JellyfinServerLayout.CacheTag), Old, "Signature: 8a477f597d28d172789f06886806bc55");
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Transcodes], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(segment));
        Assert.True(File.Exists(Path.Combine(Data, "cache", JellyfinServerLayout.CacheTag)), "the cache tag went.");
        Assert.True(File.Exists(Path.Combine(Data, "cache", "images", "resized.webp")), "the rest of the cache went.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The unrecognised case at the default folder: with neither the marker nor the cache tag, nothing
    /// shows the folder is Jellyfin's, so it is left whole.
    /// </summary>
    [Fact]
    public async Task NeverEmptiesTheDefaultFolderWithNoEvidence()
    {
        CreateData(Data);
        var file = Write(Path.Combine(Transcodes, "0b1c2d.ts"), Old);
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Transcodes, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(file), "a folder nothing showed was Jellyfin's was emptied.");
    }

    /// <summary><c>CachePath</c> in <c>system.xml</c> moves the cache, and the transcoder with it.</summary>
    [Fact]
    public async Task FollowsAMovedCacheFolder()
    {
        CreateData(Data);
        var cache = Path.Combine(_temp.Path, "JellyfinCache");
        var transcodes = Path.Combine(cache, "transcodes");
        Transcoding(transcodes);
        var image = Write(Path.Combine(cache, "images", "resized.webp"), Old);
        Write(Path.Combine(Data, "config", "system.xml"), Old, Settings("CachePath", cache));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([transcodes], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(image), "the rest of the moved cache went.");
        Assert.True(File.Exists(Path.Combine(Data, "data", "jellyfin.db")), "the database went.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A moved folder's marker outlives the server that wrote it. Settings in a data folder the
    /// installer does not record may belong to a Jellyfin that is gone, and the folder they name may
    /// be one the user uses again, so it is left whole.
    /// </summary>
    [Fact]
    public async Task NeverTrustsAMovedFolderNamedBySettingsTheInstallerDoesNotRecord()
    {
        var local = Path.Combine(_environment.LocalAppData, "jellyfin");
        CreateData(local);
        var moved = Path.Combine(_temp.Path, "Reused");
        var segment = Transcoding(moved);
        var document = Write(Path.Combine(moved, "letter.docx"), Old);
        Write(Path.Combine(local, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", moved));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("records no Jellyfin", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(document), "a folder named by an unrecorded install's settings was emptied.");
        Assert.True(File.Exists(segment));
    }

    /// <summary>
    /// A transcoder folder that holds a data folder would take the server's database and backups with
    /// it, marker or not. Jellyfin refuses to start that way, so only stale settings can say so.
    /// </summary>
    [Fact]
    public async Task NeverEmptiesATranscoderFolderThatHoldsADataFolder()
    {
        var outer = Path.Combine(_temp.Path, "Media");
        var data = Path.Combine(outer, "JellyfinData");
        var kept = CreateData(data);
        Transcoding(outer);
        Write(Path.Combine(data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", outer));
        Record(JellyfinServerLayout.DataFolderValue, data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(outer, plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("overlaps", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder folder."));
    }

    /// <summary>
    /// The same rule where the cache folder is what moved. Settings the installer does not record move
    /// the cache, and the transcoder with it, to a folder that may carry an old marker or another
    /// tool's cache tag.
    /// </summary>
    [Fact]
    public async Task NeverTrustsAMovedCacheNamedBySettingsTheInstallerDoesNotRecord()
    {
        var local = Path.Combine(_environment.LocalAppData, "jellyfin");
        CreateData(local);
        var cache = Path.Combine(_temp.Path, "Stuff");
        var segment = Transcoding(Path.Combine(cache, "transcodes"));
        Write(Path.Combine(cache, JellyfinServerLayout.CacheTag), Old, "Signature: 8a477f597d28d172789f06886806bc55");
        Write(Path.Combine(local, "config", "system.xml"), Old, Settings("CachePath", cache));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("records no Jellyfin", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(segment), "a moved cache named by an unrecorded install's settings was emptied.");
    }

    /// <summary>
    /// <c>CACHEDIR.TAG</c> is a convention other tools write too, so it counts only above the folder at
    /// the path Jellyfin's own name gives it, never above a moved one.
    /// </summary>
    [Fact]
    public async Task TheCacheTagIsNoEvidenceForAMovedCache()
    {
        CreateData(Data);
        var cache = Path.Combine(_temp.Path, "ToolCache");
        var file = Write(Path.Combine(cache, "transcodes", "0b1c2d.ts"), Old);
        Write(Path.Combine(cache, JellyfinServerLayout.CacheTag), Old, "Signature: 8a477f597d28d172789f06886806bc55");
        Write(Path.Combine(Data, "config", "system.xml"), Old, Settings("CachePath", cache));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(file), "a moved cache was emptied on another tool's cache tag.");
    }

    /// <summary>
    /// A data folder Windows would not describe still counts. A transcoder folder that holds it would
    /// take it along, and nothing here could say what was in it.
    /// </summary>
    [Fact]
    public async Task NeverEmptiesATranscoderFolderThatHoldsADataFolderWindowsWouldNotDescribe()
    {
        CreateData(Data);
        var outer = Path.Combine(_system.ProgramData, "Jellyfin");
        var service = Path.Combine(outer, "Server");
        Transcoding(outer);
        var held = Write(Path.Combine(service, "data", "jellyfin.db"), Old);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", outer));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        CleanupPlan plan;

        using (DeniedDirectory.WithUnreadableAttributes(service))
        {
            Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(service));
            plan = await CreateProvider().PlanAsync();
        }

        Assert.DoesNotContain(outer, plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("overlaps", StringComparison.Ordinal));

        await CreateProvider().ExecuteAsync(plan);

        Assert.True(File.Exists(held), "a data folder Windows would not describe went with the transcoder folder.");
    }

    /// <summary>The other direction: a transcoder folder inside what the data folder keeps.</summary>
    [Fact]
    public async Task NeverEmptiesATranscoderFolderInsideWhatTheDataFolderKeeps()
    {
        var kept = CreateData(Data);
        var inside = Path.Combine(Data, "metadata", "library");
        Transcoding(inside);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", inside));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(inside, plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder folder."));
    }

    /// <summary>
    /// Settings Deguffer cannot read may move the transcoder anywhere. The row says so and must not read
    /// as clear, and the default folder, which carries the marker, is still offered.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenItCannotReadTheEncodingSettings()
    {
        CreateData(Data);
        Transcoding(Transcodes);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, "<EncodingOptions><Transcoding");
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Transcodes], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("encoding.xml", StringComparison.Ordinal));
    }

    /// <summary>
    /// With no record, the two defaults are looked at: the per-user folder the server uses on its own,
    /// and the one a service install uses.
    /// </summary>
    [Fact]
    public async Task LooksInBothDefaultFoldersWithoutARecord()
    {
        var local = Path.Combine(_environment.LocalAppData, "jellyfin");
        var service = Path.Combine(_system.ProgramData, "Jellyfin", "Server");
        var kept = CreateData(local).Concat(CreateData(service)).ToList();
        Transcoding(Path.Combine(local, "cache", "transcodes"));
        Transcoding(Path.Combine(service, "cache", "transcodes"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [Path.Combine(local, "cache", "transcodes"), Path.Combine(service, "cache", "transcodes")],
            plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder's files."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A service install's files belong to the service account, so removing them needs elevation.</summary>
    [Fact]
    public async Task AServiceInstallsFolderNeedsElevation()
    {
        CreateData(Data);
        Transcoding(Transcodes);
        Record(JellyfinServerLayout.DataFolderValue, Data);
        Record(JellyfinServerLayout.ServiceAccountValue, "NetworkService");

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        Assert.True(step.RequiresElevation);
    }

    /// <summary>§5.3, named by the server's own process.</summary>
    [Fact]
    public async Task WarnsWhileTheServerIsRunning()
    {
        CreateData(Data);
        Transcoding(Transcodes);
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var plan = await CreateProvider(new FakeProcessInspector("jellyfin")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("jellyfin", StringComparison.Ordinal));
    }

    /// <summary>§7.1 applies no floor, so Explore may take nothing from Jellyfin's folders.</summary>
    [Fact]
    public void ExploreRemovesNothingFromJellyfinsFolders()
    {
        CreateData(Data);
        var moved = Path.Combine(_temp.Path, "JellyfinTranscodes");
        Transcoding(moved);
        Write(Path.Combine(Data, "config", "encoding.xml"), Old, Settings("TranscodingTempPath", moved));
        Record(JellyfinServerLayout.DataFolderValue, Data);

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(Path.Combine(Data, "data")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "cache")).IsAllowed);
        Assert.False(policy.MayRemove(moved).IsAllowed);
    }
}
