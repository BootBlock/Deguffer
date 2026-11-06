using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// One shell operation per batch: what each item's sink hears becomes that item's outcome and no
/// other's, a refusal of one item leaves the rest to go, and a cancel stops the batch at its next
/// item. The shell is stood in for as it was measured (<see cref="StandInShell"/>), because the real
/// one would put these files in the Recycle Bin.
/// </summary>
public sealed class ShellRecycleBinTests : IDisposable
{
    private const string VolumeName = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";

    private const string BinKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{11111111-2222-3333-4444-555555555555}";

    private readonly TempDirectory _temp = new();

    private readonly string _bin;

    public ShellRecycleBinTests() => _bin = _temp.CreateDirectory("$Recycle.Bin");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The shell deletes one item of three outright. That item alone is reported deleted outright,
    /// and each of the others is reported at the place in the bin its own sink was told.
    /// </summary>
    [Fact]
    public void AnItemTheShellDeletesOutrightIsReportedSoAloneAmongItsBatch()
    {
        var files = Files("a.bin", "b.bin", "c.bin");
        var bin = Bin(StandInShell.Telling(path =>
        {
            if (path == files[1])
            {
                File.Delete(path);
                return ShellTold.DeletedOutright;
            }

            var binned = Path.Combine(_bin, "$R" + Path.GetFileName(path));
            File.Move(path, binned);
            return ShellTold.Moved(binned);
        }));

        var outcomes = bin.Recycle(files);

        Assert.Equal([false, true, false], outcomes.Select(outcome => outcome.DeletedOutright));
        Assert.All(outcomes, outcome => Assert.True(outcome.Removed));
        Assert.Equal(Path.Combine(_bin, "$Ra.bin"), outcomes[0].Binned);
        Assert.Null(outcomes[1].Binned);
        Assert.Equal(Path.Combine(_bin, "$Rc.bin"), outcomes[2].Binned);
    }

    /// <summary>
    /// The shell refuses the middle item. It is reported refused, with the shell's number, and the
    /// items before and after it go, as they did when each item was an operation of its own.
    /// </summary>
    [Fact]
    public void AnItemTheShellRefusesLeavesTheRestOfItsBatchToGo()
    {
        var files = Files("a.bin", "b.bin", "c.bin");
        var bin = Bin(StandInShell.Telling(path =>
        {
            if (path == files[1])
            {
                return ShellTold.Failed(unchecked((int)0x80270027));
            }

            File.Delete(path);
            return ShellTold.Moved();
        }));

        var outcomes = bin.Recycle(files);

        Assert.Equal([true, false, true], outcomes.Select(outcome => outcome.Removed));
        Assert.Equal("Windows would not move this to the Recycle Bin (0x80270027). It is still where it was.", outcomes[1].Message);
        Assert.False(outcomes[1].Cancelled);
        Assert.True(File.Exists(files[1]));
    }

    /// <summary>
    /// The removal is cancelled while the first item goes. The first is reported moved, the shell
    /// reaches nothing after it, and each item after it is reported not reached and is where it was.
    /// </summary>
    [Fact]
    public void ABatchCancelledPartWayReportsWhatItMovedAndReachesNothingAfter()
    {
        var files = Files("a.bin", "b.bin", "c.bin");
        using var cts = new CancellationTokenSource();
        List<string> reached = [];
        var bin = Bin(StandInShell.Telling(path =>
        {
            reached.Add(path);
            File.Delete(path);
            cts.Cancel();
            return ShellTold.Moved();
        }));

        var outcomes = bin.Recycle(files, cts.Token);

        Assert.Equal([files[0]], reached);
        Assert.True(outcomes[0].Removed);
        Assert.False(outcomes[0].Cancelled);
        Assert.All(outcomes.Skip(1), outcome => Assert.Same(RecycleOutcome.NotReached, outcome));
        Assert.True(File.Exists(files[1]));
        Assert.True(File.Exists(files[2]));
    }

    /// <summary>
    /// The caller's rule answers that the first item stops the batch. The rule is asked before the
    /// shell reaches the second item, the shell reaches nothing after the first, and each item after
    /// it is reported stopped before and is where it was, never as cancelled or refused.
    /// </summary>
    [Fact]
    public void ACallersRuleStopsTheBatchAtTheItemItAnswersFor()
    {
        var files = Files("a.bin", "b.bin", "c.bin");
        List<string> reached = [];
        List<(int Index, int Reached)> asked = [];
        var bin = Bin(StandInShell.Telling(path =>
        {
            reached.Add(path);
            File.Delete(path);
            return ShellTold.DeletedOutright;
        }));

        // Recorded rather than asserted: the rule runs on the bin's own thread, where a throw would
        // end the test process.
        var outcomes = bin.Recycle(files, stopsAfter: (index, outcome) =>
        {
            asked.Add((index, reached.Count));
            return outcome.DeletedOutright;
        });

        Assert.Equal([(0, 1)], asked);
        Assert.Equal([files[0]], reached);
        Assert.True(outcomes[0].DeletedOutright);
        Assert.All(outcomes.Skip(1), outcome => Assert.Same(RecycleOutcome.StoppedBefore, outcome));
        Assert.True(File.Exists(files[1]));
        Assert.True(File.Exists(files[2]));
    }

    /// <summary>A removal cancelled before the batch goes hands the shell nothing, and every item is not reached.</summary>
    [Fact]
    public void ARemovalCancelledBeforeItsBatchHandsTheShellNothing()
    {
        var files = Files("a.bin", "b.bin");
        List<string> handed = [];

        var outcomes = Bin(StandInShell.NeverAsked(handed)).Recycle(files, new CancellationToken(canceled: true));

        Assert.Empty(handed);
        Assert.All(outcomes, outcome => Assert.Same(RecycleOutcome.NotReached, outcome));
        Assert.All(files, file => Assert.True(File.Exists(file)));
    }

    /// <summary>
    /// A batch past the bound is refused before anything is asked, because the bound is what keeps
    /// the wait between asking and moving short, and what a duplicate removal holds open.
    /// </summary>
    [Fact]
    public void ABatchPastTheBoundIsRefusedBeforeTheShellIsAsked()
    {
        var file = Files("a.bin")[0];
        List<string> handed = [];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Bin(StandInShell.NeverAsked(handed)).Recycle(Enumerable.Repeat(file, IRecycleBin.LargestBatch + 1).ToList()));

        Assert.Empty(handed);
    }

    /// <summary>
    /// A confirmation of many items on one volume reads that volume's bin once, and so does the move
    /// of them, rather than once an item: each reading sums everything the bin holds, and grows with
    /// each item the move sends it.
    /// </summary>
    [Fact]
    public void EachVolumesBinIsReadOnceForAConfirmationAndOnceForTheMove()
    {
        var files = Files([.. Enumerable.Range(0, 40).Select(i => $"{i}.bin")]);
        List<string> read = [];
        var bin = Bin(StandInShell.Telling(path =>
        {
            var binned = Path.Combine(_bin, "$R" + Path.GetFileName(path));
            File.Move(path, binned);
            return ShellTold.Moved(binned);
        }), held: volume => { read.Add(volume); return 0; });

        Assert.All(bin.WhyItCannotTake(files), Assert.Null);
        Assert.Equal([VolumeName], read);

        var outcomes = bin.Recycle(files);

        Assert.All(outcomes, outcome => Assert.True(outcome.Removed));
        Assert.Equal([VolumeName, VolumeName], read);
    }

    /// <summary>
    /// The bin is set to keep nothing after the confirmation said it could take every item. The move
    /// reads the bin again, refuses every item, and hands the shell none of them, so each is still
    /// where it was, with the file beside them (§7.1, §5.6).
    /// </summary>
    [Fact]
    public void ABinSetToKeepNothingAfterTheConfirmationTakesNothingAtTheMove()
    {
        var files = Files("a.bin", "b.bin", "c.bin");
        var beside = _temp.CreateFile(10, "beside.bin");
        var settings = BinSettings();
        List<string> handed = [];
        var bin = Bin(StandInShell.NeverAsked(handed), settings: settings);

        Assert.All(bin.WhyItCannotTake(files), Assert.Null);

        settings.WithRegistryNumber(BinKey, "NukeOnDelete", 1);
        var outcomes = bin.Recycle(files);

        Assert.Empty(handed);
        Assert.All(outcomes, outcome =>
        {
            Assert.False(outcome.Removed);
            Assert.Contains("set to delete what it is sent", outcome.Message, StringComparison.Ordinal);
        });
        Assert.All([.. files, beside], file => Assert.True(File.Exists(file), file));
    }

    private string[] Files(params string[] names) => [.. names.Select(name => _temp.CreateFile(10, name))];

    /// <summary>The settings of a bin that may hold a gigabyte and keeps what it is sent.</summary>
    private static FakeUserEnvironment BinSettings() =>
        new FakeUserEnvironment(Path.GetTempPath())
            .WithRegistryNumber(BinKey, "MaxCapacity", 1024)
            .WithRegistryNumber(BinKey, "NukeOnDelete", 0);

    /// <summary>
    /// The bin, through <paramref name="perform"/>, on a volume whose bin holds nothing and may hold a
    /// gigabyte, so only the shell decides, unless a test gives it other settings.
    /// </summary>
    /// <param name="held">What reading the volume's bin answers, which a test counts.</param>
    private ShellRecycleBin Bin(RecycleOperation perform, Func<string, long?>? held = null, FakeUserEnvironment? settings = null) => new(
        perform,
        new RecycleBinReach(
            new FakeVolumeInventory().With(_temp.Path + Path.DirectorySeparatorChar, volumeName: VolumeName),
            new RecycleBinRooms(held ?? (_ => 0), settings ?? BinSettings()),
            WindowsFileSystem.Default,
            WalkTuning.Default));
}
