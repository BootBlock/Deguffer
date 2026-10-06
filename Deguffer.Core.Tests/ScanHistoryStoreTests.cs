using System.IO.Compression;
using System.Text;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The kept scan summaries (#260): that one round-trips, that one that cannot be read is passed over
/// rather than compared, how many are kept, and that removing them removes nothing else.
/// </summary>
public sealed class ScanHistoryStoreTests : IDisposable
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();

    private readonly FakeUserEnvironment _environment;

    private readonly ScanHistoryStore _store;

    public ScanHistoryStoreTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _store = new ScanHistoryStore(_environment);
    }

    private string Folder => Path.Combine(_environment.LocalAppData, "Deguffer", "History", "{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ASummaryRoundTrips()
    {
        var summary = Summary(Then, walked: true);

        Assert.True(_store.Save(summary));

        var kept = Assert.Single(_store.List(Volume));
        Assert.Equal(Volume, kept.Volume);
        Assert.Equal(@"C:\", kept.RootPath);
        Assert.Equal(Then, kept.TakenUtc);
        Assert.Equal(ScanStrategy.ParallelEnumeration, kept.Strategy);
        Assert.Equal(summary.UsedBytes, kept.UsedBytes);

        var read = _store.Read(kept);
        Assert.NotNull(read);
        Assert.Equal(summary with { Folders = [] }, read with { Folders = [] });
        Assert.Equal(summary.Folders, read.Folders);
        Assert.Equal(Then, Assert.Single(_store.List()).TakenUtc);
    }

    /// <summary>Each way a file can be unusable is listed as nothing and read as nothing.</summary>
    [Theory]
    [InlineData("not gzip")]
    [InlineData("torn")]
    [InlineData("future format")]
    [InlineData("parent after child")]
    [InlineData("path in a name")]
    [InlineData("negative size")]
    [InlineData("another volume")]
    public void AnUnreadableSummaryIsIgnoredRatherThanCompared(string damage)
    {
        Assert.True(_store.Save(Summary(Then)));
        var file = Assert.Single(Directory.GetFiles(Folder));
        var json = Json(file);

        switch (damage)
        {
            case "not gzip":
                File.WriteAllText(file, json);
                break;
            case "torn":
                var whole = File.ReadAllBytes(file);
                File.WriteAllBytes(file, whole[..(whole.Length / 2)]);
                break;
            case "future format":
                WriteJson(file, json.Replace("\"Format\":1", "\"Format\":2", StringComparison.Ordinal));
                break;
            case "parent after child":
                WriteJson(file, json.Replace("[1,\"me\"", "[5,\"me\"", StringComparison.Ordinal));
                break;
            case "path in a name":
                WriteJson(file, json.Replace("\"me\"", "\"..\\\\Windows\"", StringComparison.Ordinal));
                break;
            case "negative size":
                WriteJson(file, json.Replace($"\"me\",{50 * Megabyte}", "\"me\",-1", StringComparison.Ordinal));
                break;
            case "another volume":
                WriteJson(file, json.Replace("0b6f5c1e", "1b6f5c1e", StringComparison.Ordinal));
                break;
        }

        Assert.NotEqual(json, Json(file, quiet: true));

        // A header that still reads, with folders that do not, is listed and read as nothing.
        foreach (var kept in _store.List(Volume))
        {
            Assert.Null(_store.Read(kept));
        }

        var history = new ScanHistory(_store);
        var record = history.Record(ExploreScan.Fast(Tree()), Volume, new VolumeSpace(1000 * Megabyte, 400 * Megabyte), Then.AddDays(1));

        Assert.Null(record.Growth);
    }

    /// <summary>A damaged newest summary gives way to the one before it, which still describes the volume.</summary>
    [Fact]
    public void ADamagedNewestSummaryGivesWayToTheOneBeforeIt()
    {
        Assert.True(_store.Save(Summary(Then)));
        Assert.True(_store.Save(Summary(Then.AddDays(7))));

        var newest = Directory.GetFiles(Folder).Order(StringComparer.Ordinal).Last();
        WriteJson(newest, Json(newest).Replace("\"Folders\":[[", "\"Folders\":[[\"x\",", StringComparison.Ordinal));

        var record = new ScanHistory(_store).Record(
            ExploreScan.Fast(Tree()), Volume, new VolumeSpace(1000 * Megabyte, 400 * Megabyte), Then.AddDays(14));

        Assert.NotNull(record.Growth);
        Assert.Equal(Then, record.Growth.SinceUtc);
        Assert.True(record.Saved);
    }

    [Fact]
    public void OnlyTheNewestSummariesOfAVolumeAreKept()
    {
        for (var day = 0; day < ScanHistoryStore.KeptPerVolume + 3; day++)
        {
            Assert.True(_store.Save(Summary(Then.AddDays(day))));
        }

        var kept = _store.List(Volume);

        Assert.Equal(ScanHistoryStore.KeptPerVolume, kept.Count);
        Assert.Equal(Then.AddDays(3), kept[0].TakenUtc);
        Assert.Equal(Then.AddDays(ScanHistoryStore.KeptPerVolume + 2), kept[^1].TakenUtc);
    }

    /// <summary>A volume Windows did not name by GUID has no folder of its own, so nothing is kept for it.</summary>
    [Fact]
    public void AVolumeWithoutAGuidIsNotKept()
    {
        Assert.False(_store.Save(Summary(Then) with { Volume = @"C:\..\..\Windows\" }));
        Assert.False(Directory.Exists(Path.Combine(_environment.LocalAppData, "Deguffer", "History")));
    }

    /// <summary>
    /// Removing one summary removes that one, and removing all removes only what this store wrote:
    /// a file it did not write, in a folder it did, survives, and so does the folder holding it.
    /// </summary>
    [Fact]
    public void RemovingSummariesRemovesNothingElse()
    {
        Assert.True(_store.Save(Summary(Then)));
        Assert.True(_store.Save(Summary(Then.AddDays(1))));

        var stranger = Path.Combine(Folder, "notes.txt");
        File.WriteAllText(stranger, "not a summary");
        var beside = Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json");
        File.WriteAllText(beside, "{}");

        var history = new ScanHistory(_store);
        var changed = 0;
        history.Changed += (_, _) => changed++;

        Assert.True(history.Remove(history.Kept(Volume)[0]));
        Assert.Equal(Then.AddDays(1), Assert.Single(history.Kept(Volume)).TakenUtc);
        Assert.Equal(1, changed);

        Assert.True(history.RemoveAll());
        Assert.Empty(history.Kept(Volume));
        Assert.Empty(history.All());
        Assert.Equal(2, changed);

        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(beside));
        Assert.True(Directory.Exists(Folder));
    }

    /// <summary>A summary handed back for removal that names a file this store would not have written is refused.</summary>
    [Fact]
    public void ASummaryNamingAnotherFileIsNotRemoved()
    {
        Assert.True(_store.Save(Summary(Then)));
        var kept = _store.List(Volume)[0];

        var outside = Path.Combine(_temp.Path, "20260901T090000000Z.json.gz");
        File.WriteAllText(outside, "keep me");

        Assert.False(_store.Remove(kept with { File = outside }));
        Assert.True(File.Exists(outside));
    }

    private static ScanSummary Summary(DateTime taken, bool walked = false)
    {
        var scan = walked ? ExploreScan.Walked(Tree(), FallbackReason.NotElevated) : ExploreScan.Fast(Tree());

        return ScanSummaries.Take(scan, Volume, new VolumeSpace(1000 * Megabyte, 400 * Megabyte), taken);
    }

    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("Users", IsDirectory: true, IsLink: false, Size: 0)]);
        var me = builder.AddChildren(users, [new ExploreChild("me", IsDirectory: true, IsLink: false, Size: 0)]);
        builder.AddChildren(me, [new ExploreChild("data.bin", IsDirectory: false, IsLink: false, Size: 50 * Megabyte)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static string Json(string file, bool quiet = false)
    {
        try
        {
            using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (InvalidDataException) when (quiet)
        {
            return string.Empty;
        }
        catch (EndOfStreamException) when (quiet)
        {
            return string.Empty;
        }
    }

    private static void WriteJson(string file, string json)
    {
        using var gzip = new GZipStream(File.Create(file), CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(json));
    }
}
