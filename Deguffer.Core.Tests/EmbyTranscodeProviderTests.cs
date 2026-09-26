using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Emby Server's transcoder leftovers. Emby writes into <c>transcoding-temp</c> inside whatever folder
/// its settings name, so what has to be shown is that the named folder itself is never emptied.
/// </summary>
public sealed class EmbyTranscodeProviderTests : IDisposable
{
    private static readonly TimeSpan Old = TimeSpan.FromHours(MediaServerTranscodeProvider.QuietHours + 12);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EmbyTranscodeProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Data => EmbyServerLayout.ProgramData(_environment);

    private string TranscodingTemp => Path.Combine(Data, EmbyServerLayout.TranscodeFolderName);

    private EmbyTranscodeProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private static string Write(string file, TimeSpan age, string text = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.Length > 0 ? text : new string('x', 4096));
        return TempDirectory.Age(file, age);
    }

    private string MoveTranscoder(string folder) => Write(
        Path.Combine(Data, "config", "encoding.xml"),
        Old,
        $"<EncodingOptions><TranscodingTempPath>{folder}</TranscodingTempPath></EncodingOptions>");

    private string[] CreateData() =>
    [
        Write(Path.Combine(Data, "config", "system.xml"), Old, "<ServerConfiguration />"),
        Write(Path.Combine(Data, "data", "library.db"), Old),
        Write(Path.Combine(Data, "metadata", "library", "poster.jpg"), Old),
        Write(Path.Combine(Data, "plugins", "configurations", "plugin.xml"), Old),
        Write(Path.Combine(Data, "root", "default", "Movies", "options.xml"), Old),
        Write(Path.Combine(Data, "cache", "images", "resized.jpg"), Old),
    ];

    [Fact]
    public async Task ReportsNotPresentWhenEmbyHasNeverRunHere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>§5.2 and §5.6, proved by running a plan.</summary>
    [Fact]
    public async Task EmptiesTranscodingTempAloneAndAssertsEmbysDataSurvived()
    {
        var kept = CreateData();
        var segment = Write(Path.Combine(TranscodingTemp, "a1b2c3.ts"), Old);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([TranscodingTemp], plan.TargetedPaths);

        foreach (var name in new[] { "", "config", "data", "metadata", "plugins", "root" })
        {
            var path = name.Length == 0 ? Data : Path.Combine(Data, name);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(segment));
        Assert.True(Directory.Exists(TranscodingTemp), "the transcoding-temp folder itself went.");
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the transcoder's files."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.3. A segment written two minutes ago belongs to a film playing now.</summary>
    [Fact]
    public async Task ASegmentAFilmIsPlayingFromSurvivesTheRun()
    {
        CreateData();
        var dead = Write(Path.Combine(TranscodingTemp, "a1b2c3.ts"), Old);
        var live = Write(Path.Combine(TranscodingTemp, "d4e5f6.ts"), TimeSpan.FromMinutes(2));

        var provider = CreateProvider();
        await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(live), "a segment a film was playing from was deleted.");
    }

    /// <summary>
    /// The folder the setting names is the user's, and Emby's help says it deletes everything in the
    /// folder it transcodes to. Only the <c>transcoding-temp</c> folder Emby made inside it is emptied.
    /// The default folder is still offered, because Emby left segments there before the move.
    /// </summary>
    [Fact]
    public async Task EmptiesOnlyTranscodingTempInsideAMovedFolder()
    {
        CreateData();
        var moved = Path.Combine(_temp.Path, "Scratch");
        var movedTemp = Path.Combine(moved, EmbyServerLayout.TranscodeFolderName);
        var segment = Write(Path.Combine(movedTemp, "a1b2c3.ts"), Old);
        var beside = Write(Path.Combine(moved, "holiday.mkv"), Old);
        Write(Path.Combine(TranscodingTemp, "old.ts"), Old);
        MoveTranscoder(moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([TranscodingTemp, movedTemp], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(segment));
        Assert.True(File.Exists(beside), "a file in the folder Emby's settings name was deleted.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>The unrecognised case: a moved folder with no <c>transcoding-temp</c> in it offers nothing.</summary>
    [Fact]
    public async Task AMovedFolderWithoutTranscodingTempOffersNothing()
    {
        CreateData();
        var moved = Path.Combine(_temp.Path, "Scratch");
        var beside = Write(Path.Combine(moved, "holiday.mkv"), Old);
        MoveTranscoder(moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(beside));
    }

    [Fact]
    public async Task SaysSoWhenItCannotPlaceTheTranscoderSetting()
    {
        CreateData();
        Write(Path.Combine(TranscodingTemp, "a1b2c3.ts"), Old);
        MoveTranscoder(@"relative\folder");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([TranscodingTemp], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("encoding.xml", StringComparison.Ordinal));
    }

    /// <summary>§5.3, named by the server's own process.</summary>
    [Fact]
    public async Task WarnsWhileTheServerIsRunning()
    {
        CreateData();
        Write(Path.Combine(TranscodingTemp, "a1b2c3.ts"), Old);

        var plan = await CreateProvider(new FakeProcessInspector("EmbyServer")).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("EmbyServer", StringComparison.Ordinal));
    }

    /// <summary>§7.1 applies no floor, so Explore may take nothing from Emby's folders.</summary>
    [Fact]
    public void ExploreRemovesNothingFromEmbysFolders()
    {
        CreateData();
        var moved = Path.Combine(_temp.Path, "Scratch");
        var movedTemp = Path.Combine(moved, EmbyServerLayout.TranscodeFolderName);
        Write(Path.Combine(movedTemp, "a1b2c3.ts"), Old);
        var beside = Write(Path.Combine(moved, "holiday.mkv"), Old);
        MoveTranscoder(moved);

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(TranscodingTemp).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "data")).IsAllowed);
        Assert.False(policy.MayRemove(movedTemp).IsAllowed);
        Assert.True(policy.MayRemove(beside).IsAllowed);
    }
}
