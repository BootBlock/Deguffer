using System.Diagnostics;
using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real Cloud Files calls, against a sync root this suite registers and connects itself.
///
/// <para>Every claim <see cref="ICloudFiles"/> makes is one the rest of the suite relies on through
/// <see cref="FakeCloudFiles"/>, so each is observed here once: that a listing tells a placeholder from
/// an ordinary file, that describing one downloads nothing, and that a release changes the one pin it
/// was asked to and no other. Nothing here touches a real sync app's root.</para>
/// </summary>
public sealed class CloudFilesTests : IDisposable
{
    private const int Megabyte = 1024 * 1024;

    /// <summary><c>FILE_ATTRIBUTE_UNPINNED</c>, which <c>CfSetPinState</c> sets on an ordinary file too.</summary>
    private const FileAttributes Unpinned = (FileAttributes)0x0010_0000;

    private readonly TempDirectory _temp = new();
    private readonly ScratchSyncRoot _root;
    private readonly ICloudFiles _cloud = CloudFiles.Default;

    public CloudFilesTests() => _root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

    public void Dispose()
    {
        _root.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// Windows leaves a root under <c>AppData\Local</c> out of its list, and the suite's scratch folder is
    /// under it, so this one root is registered in the profile folder instead and removed afterwards.
    /// </summary>
    [Fact]
    public void ListsARootUnderTheNameItsSyncAppRegistered()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deguffer-tests");
        var path = Path.Combine(parent, Scratch.NewIdentifier());

        try
        {
            using var root = new ScratchSyncRoot(path);

            var listed = Assert.Single(_cloud.SyncRoots(), candidate => candidate.Id == root.Id);

            Assert.Equal(path, listed.Path, ignoreCase: true);
            Assert.Equal(ScratchSyncRoot.ProviderName, listed.ProviderName);
            Assert.DoesNotContain(_cloud.SyncRoots(), candidate => candidate.Id == _root.Id);
        }
        finally
        {
            Directory.Delete(path, recursive: true);

            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    [Fact]
    public void ReportsTheSyncAppAsRunningOnlyWhileItIsConnected()
    {
        Assert.Equal(SyncProviderState.Running, _cloud.ProviderState(_root.Path));

        _root.Disconnect();

        Assert.Equal(SyncProviderState.NotRunning, _cloud.ProviderState(_root.Path));
    }

    [Fact]
    public void AFolderThatIsNoSyncRootIsNotReportedAsRunning()
    {
        Assert.Equal(SyncProviderState.Unknown, _cloud.ProviderState(_temp.CreateDirectory("Ordinary")));
    }

    [Fact]
    public void TellsAPlaceholderFromAnOrdinaryFileByTheListingAlone()
    {
        var local = _root.LocalCopy("local.bin", Megabyte);
        var plain = _root.PlainFile("plain.bin", Megabyte);
        var folder = _root.Folder("Folder");

        var listed = _cloud.List(_root.Path, CancellationToken.None).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        Assert.True(listed[local].IsPlaceholder);
        Assert.False(listed[plain].IsPlaceholder);
        Assert.True(listed[folder] is { IsPlaceholder: true, IsDirectory: true, IsOtherLink: false });
        Assert.All(listed.Values, entry => Assert.False(entry.IsOtherLink));
    }

    [Fact]
    public void DescribesEachPlaceholderState()
    {
        var local = _root.LocalCopy("local.bin", Megabyte);
        var edited = _root.LocalCopy("edited.bin", Megabyte);
        File.AppendAllText(edited, "a local edit");
        var pinned = _root.LocalCopy("pinned.bin", Megabyte);
        _root.Pin(pinned, PinState.Pinned);

        Assert.Equal(new { OnDisk = (long)Megabyte, Modified = 0L, InSync = true, Pin = PinState.Unspecified }, Shape(local));
        Assert.True(_cloud.Read(edited).Placeholder is { InSync: false, ModifiedBytes: > 0 });
        Assert.Equal(PinState.Pinned, _cloud.Read(pinned).Placeholder!.Pin);
    }

    [Fact]
    public void ReadsAnOrdinaryFileAsPresentAndNotAPlaceholder()
    {
        var reading = _cloud.Read(_root.PlainFile("plain.bin", 10));

        Assert.Equal(PathPresence.Present, reading.Presence);
        Assert.Null(reading.Placeholder);
        Assert.Equal(PathPresence.Absent, _cloud.Read(_root.At("missing.bin")).Presence);
    }

    /// <summary>
    /// Nothing answers a download request here, so a read that tried to fetch the data would wait for
    /// the platform's own time-out, a minute or more, and then fail. Answering at once with nothing on
    /// the disk is what shows the listing and the description read no data.
    /// </summary>
    [Fact]
    public void DescribingAnOnlineOnlyFileDownloadsNothing()
    {
        var online = _root.OnlineOnly("online.bin", 5 * Megabyte);
        var clock = Stopwatch.StartNew();

        var listed = Assert.Single(_cloud.List(_root.Path, CancellationToken.None));
        var reading = _cloud.Read(online);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Describing it took {clock.Elapsed}.");
        Assert.True(listed.IsPlaceholder);
        Assert.Equal(0, reading.Placeholder!.OnDiskBytes);
        Assert.Equal(0, _cloud.Read(online).Placeholder!.OnDiskBytes);
    }

    [Fact]
    public void ReleaseUnpinsAnEligiblePlaceholderAndReportsWhatItHeld()
    {
        var local = _root.LocalCopy("local.bin", Megabyte);

        var answer = _cloud.Release(local, _ => true);

        Assert.Equal(new ReleaseAnswer(ReleaseResult.Requested, Megabyte), answer);
        Assert.Equal(PinState.Unpinned, _cloud.Read(local).Placeholder!.Pin);
        Assert.True(File.Exists(local));
    }

    /// <summary>The rule is asked of the file as it is, through the handle that would unpin it.</summary>
    [Fact]
    public void ReleaseLeavesAFileTheRuleNoLongerAllows()
    {
        var edited = _root.LocalCopy("edited.bin", Megabyte);
        File.AppendAllText(edited, "a local edit");

        var answer = _cloud.Release(edited, now => now.InSync && now.ModifiedBytes == 0);

        Assert.Equal(ReleaseResult.NoLongerEligible, answer.Result);
        Assert.Equal(PinState.Unspecified, _cloud.Read(edited).Placeholder!.Pin);
    }

    /// <summary>
    /// <c>CfSetPinState</c> itself sets the unpinned attribute on an ordinary file without complaint, as
    /// a probe showed. The release must never reach it, whatever the rule says.
    /// </summary>
    [Fact]
    public void ReleaseNeverUnpinsAnOrdinaryFile()
    {
        var plain = _root.PlainFile("plain.bin", Megabyte);

        var answer = _cloud.Release(plain, _ => true);

        Assert.Equal(ReleaseResult.NoLongerEligible, answer.Result);
        Assert.False(File.GetAttributes(plain).HasFlag(Unpinned));
    }

    [Fact]
    public void ReleaseOfAFileThatHasGoneSaysSo()
    {
        Assert.Equal(ReleaseResult.Gone, _cloud.Release(_root.At("missing.bin"), _ => true).Result);
    }

    /// <summary>
    /// The walk and the rules over real placeholders: a folder's pin reaches the files inside it, which
    /// report no pin of their own, and only the one file that should go is chosen.
    /// </summary>
    [Fact]
    public void TheWalkChoosesOnlyWhatTheRulesAllowOnARealRoot()
    {
        var goes = _root.LocalCopy("goes.bin", Megabyte);
        _root.LocalCopy("edited.bin", Megabyte);
        File.AppendAllText(_root.At("edited.bin"), "a local edit");
        _root.PlainFile("plain.bin", Megabyte);
        _root.OnlineOnly("online.bin", Megabyte);
        var kept = _root.Folder("Kept");
        _root.LocalCopy(Path.Combine("Kept", "inside.bin"), Megabyte);
        _root.Pin(kept, PinState.Pinned);

        Assert.Equal(PinState.Unspecified, _cloud.Read(_root.At("Kept", "inside.bin")).Placeholder!.Pin);

        var selection = PlaceholderWalk.Of(_cloud, _root.Path, MinimumAge.Off, CancellationToken.None);

        Assert.Equal([goes], selection.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, selection.HeldFor(HeldBack.Pinned).Files);
        Assert.Equal(1, selection.HeldFor(HeldBack.NotInSync).Files);
    }

    private object Shape(string path) => _cloud.Read(path).Placeholder is { } p
        ? new { OnDisk = p.OnDiskBytes, Modified = p.ModifiedBytes, p.InSync, p.Pin }
        : throw new InvalidOperationException($"{path} is not a placeholder.");
}
