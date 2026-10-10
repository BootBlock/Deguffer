using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The items Windows' Recycle Bin cannot take, which the shell deletes outright while it reports
/// success, so they never reach it (§7.1, §7.4: a removal the bin will not take fails, and is never
/// deleted outright in its place).
/// </summary>
public sealed class RecycleBinReachTests : IDisposable
{
    private const string VolumeName = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";

    private const string BinKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{11111111-2222-3333-4444-555555555555}";

    private readonly TempDirectory _temp = new();

    /// <summary>A volume whose bin may hold a gigabyte, so only the paths decide.</summary>
    private readonly RecycleBinReach _reach;

    public RecycleBinReachTests() => _reach = Reach(Limit(megabytes: 1024));

    public void Dispose() => _temp.Dispose();

    /// <summary>What the bin on the volume the scratch tree is on can take, as <paramref name="settings"/> say.</summary>
    /// <param name="held">What the bin holds, or null where Windows will not say; empty where not given.</param>
    private RecycleBinReach Reach(FakeUserEnvironment settings, Func<string, long?>? held = null) =>
        new(
            new FakeVolumeInventory().With(_temp.Path + Path.DirectorySeparatorChar, volumeName: VolumeName),
            new RecycleBinRooms(held ?? (_ => 0), settings));

    private static FakeUserEnvironment Limit(int megabytes, bool keepsNothing = false) =>
        new FakeUserEnvironment(Path.GetTempPath())
            .WithRegistryNumber(BinKey, "MaxCapacity", megabytes)
            .WithRegistryNumber(BinKey, "NukeOnDelete", keepsNothing ? 1 : 0);

    /// <summary>
    /// The shell drops trailing dots from every name on a path it parses, and trailing spaces from
    /// the last, so a
    /// <c>report.</c> handed to the bin moves the <c>report</c> beside it, and a file inside
    /// <c>sub.</c> moves its namesake inside <c>sub</c>. Each is refused, and the sibling whose name
    /// the shell reads correctly is still taken.
    /// </summary>
    [Theory]
    [InlineData("report.", "report")]
    [InlineData("notes ", "notes")]
    [InlineData(@"sub.\report", @"sub\report")]
    [InlineData(@"sub..\report", @"sub\report")]
    public void AnItemWithANameTheShellRereadsIsRefused(string name, string read)
    {
        var item = Path.Combine(_temp.Path, name);
        var sibling = Path.Combine(_temp.Path, read);

        foreach (var file in new[] { item, sibling })
        {
            Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(file)!));
            File.WriteAllText(LongPath.Extended(file), "x");
        }

        Assert.True(File.Exists(LongPath.Extended(item)), "the fixture holds the name as spelled");
        Assert.Contains("ends in a dot or a space", _reach.WhyNot(item));
        Assert.Null(_reach.WhyNot(sibling));
    }

    /// <summary>
    /// The shell keeps a trailing space on a folder above the item, so such an item is taken: the
    /// refusal above is for names the shell rereads, not every unusual one.
    /// </summary>
    [Fact]
    public void AnItemInAFolderWhoseNameEndsInASpaceIsTaken()
    {
        var item = Path.Combine(_temp.Path, "sp ", "report");
        Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(item)!));
        File.WriteAllText(LongPath.Extended(item), "x");

        Assert.Null(_reach.WhyNot(item));
    }

    [Fact]
    public void AFileWhosePathIsTheLongestTheBinTakesIsTaken()
    {
        var file = FileOfLength(RecycleBinReach.LongestPath, "file");

        Assert.Null(_reach.WhyNot(file));
    }

    [Fact]
    public void AFileWhosePathIsOneCharacterLongerIsRefused()
    {
        var file = FileOfLength(RecycleBinReach.LongestPath + 1, "file");

        Assert.Contains("260 characters long", _reach.WhyNot(file));
    }

    /// <summary>
    /// The shell deletes a folder outright, whole, when one path anywhere inside it is too long, so
    /// a folder whose own path is short is refused for what it holds.
    /// </summary>
    [Fact]
    public void AFolderHoldingAPathTooLongForTheBinIsRefused()
    {
        var folder = _temp.CreateDirectory("folder");
        _temp.CreateFile(10, "folder", "short.txt");
        FileOfLength(RecycleBinReach.LongestPath + 1, Path.Combine("folder", "deep"));

        Assert.Contains("whose path is 260 characters long", _reach.WhyNot(folder));
    }

    [Fact]
    public void AFolderWhosePathsAreAllShortEnoughIsTaken()
    {
        var folder = _temp.CreateDirectory("folder");
        FileOfLength(RecycleBinReach.LongestPath, Path.Combine("folder", "deep"));

        Assert.Null(_reach.WhyNot(folder));
    }

    /// <summary>A link inside a folder is moved as a link, so what lies on its far side is not the folder's.</summary>
    [Fact]
    public void ALinkInsideAFolderIsNotLookedThrough()
    {
        var folder = _temp.CreateDirectory("folder");
        var target = _temp.CreateDirectory("target");
        FileOfLength(RecycleBinReach.LongestPath + 40, Path.Combine("target", "deep"));
        Junction.ToDirectory(Path.Combine(folder, "link"), target);

        Assert.Null(_reach.WhyNot(folder));
    }

    /// <summary>The shell deletes outright a file longer than its drive's bin can hold, and compares the length.</summary>
    [Fact]
    public void AFileLongerThanItsBinsLimitIsRefusedAndOneAsLongIsTaken()
    {
        var reach = Reach(Limit(megabytes: 1));
        var fits = _temp.CreateFile(1024 * 1024, "fits.bin");
        var over = _temp.CreateFile(1024 * 1024 + 1, "over.bin");

        Assert.Null(reach.WhyNot(fits));
        Assert.Contains("more than this drive's Recycle Bin can hold", reach.WhyNot(over));
    }

    /// <summary>The shell deletes a folder outright, whole, where its files together are longer than the bin can hold.</summary>
    [Fact]
    public void AFolderWhoseFilesTogetherAreLongerThanItsBinsLimitIsRefused()
    {
        var reach = Reach(Limit(megabytes: 1));
        var folder = _temp.CreateDirectory("folder");
        _temp.CreateFile(600 * 1024, "folder", "a.bin");
        _temp.CreateFile(600 * 1024, "folder", "inner", "b.bin");

        Assert.Contains("more than this drive's Recycle Bin can hold", reach.WhyNot(folder));
    }

    /// <summary>
    /// Where nothing shows what the bin can take, the item is refused: a bin with no limit Windows
    /// will say, a drive with no bin Windows will describe (a removable drive has none), and a drive
    /// this cannot place. The shell would delete the item outright if the bin could not take it.
    /// </summary>
    [Fact]
    public void AnItemIsRefusedWhereNothingShowsItsBinCanTakeIt()
    {
        var file = _temp.CreateFile(10, "small.bin");
        var noSettings = new FakeUserEnvironment(Path.GetTempPath());

        Assert.Contains("would not say how much", Reach(noSettings).WhyNot(file));
        Assert.Contains("or the drive has none", Reach(Limit(megabytes: 1024), held: _ => null).WhyNot(file));
        Assert.Contains("cannot tell which drive", new RecycleBinReach(new FakeVolumeInventory(), new RecycleBinRooms(_ => 0, Limit(1024))).WhyNot(file));
    }

    /// <summary>A folder Windows will not list all the way down may hold a path the bin cannot take, so it is refused.</summary>
    [Fact]
    public void AFolderWindowsWillNotListAllTheWayDownIsRefused()
    {
        var folder = _temp.CreateDirectory("folder");
        var inner = _temp.CreateDirectory("folder", "inner");
        _temp.CreateFile(10, "folder", "inner", "a.bin");

        using var denied = new DeniedDirectory(inner);

        Assert.Contains("would not list", _reach.WhyNot(folder));
    }

    [Fact]
    public void NothingGoesToABinSetToKeepNothing()
    {
        var file = _temp.CreateFile(10, "small.bin");

        Assert.Contains("set to delete what it is sent", Reach(Limit(megabytes: 1024, keepsNothing: true)).WhyNot(file));
    }

    /// <summary>
    /// The shell never sees an item the bin cannot take, so it cannot delete it outright. The shell's
    /// move is stood in for, because the real one would delete this file.
    /// </summary>
    [Fact]
    public void TheShellIsNeverHandedAnItemTheBinCannotTake()
    {
        var file = FileOfLength(RecycleBinReach.LongestPath + 1, "file");
        List<string> handed = [];
        var bin = new ShellRecycleBin(
            path =>
            {
                handed.Add(path);
                File.Delete(LongPath.Extended(path));
                return new RecycleOutcome(Removed: true);
            },
            _reach);

        var outcome = bin.Recycle(file);

        Assert.False(outcome.Removed);
        Assert.Empty(handed);
        Assert.True(File.Exists(LongPath.Extended(file)));

        // The reason the bin gives, then what became of the item, so a refusal at the removal says both.
        Assert.StartsWith(bin.WhyItCannotTake(file)!, outcome.Message, StringComparison.Ordinal);
        Assert.EndsWith("it is still where it was.", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell reporting a deleted item and no item in the bin is Microsoft's word that it was not
    /// recycled, so the outcome says it was deleted outright, never that the bin holds it.
    /// </summary>
    [Fact]
    public void AnItemTheShellDeletedWithNoItemInTheBinIsReportedAsDeletedOutright()
    {
        var sink = new BinnedItem();
        sink.PostDeleteItem(flags: 0, item: null!, result: 0, created: null);

        var outcome = ShellRecycleBin.Outcome(aborted: false, sink);

        Assert.True(outcome.DeletedOutright);
        Assert.True(outcome.Removed);
        Assert.Null(outcome.Binned);
        Assert.Contains("outright", outcome.Message);
    }

    [Fact]
    public void AnItemTheShellFailedToDeleteIsNotReportedAsDeletedOutright()
    {
        var sink = new BinnedItem();
        sink.PostDeleteItem(flags: 0, item: null!, result: unchecked((int)0x80070020), created: null);

        Assert.False(ShellRecycleBin.Outcome(aborted: true, sink).DeletedOutright);
    }

    /// <summary>A file under <paramref name="folder"/> whose full path, in display form, is exactly <paramref name="length"/> characters.</summary>
    private string FileOfLength(int length, string folder)
    {
        var path = Path.Combine(_temp.Path, folder);

        // Folders of 100 characters, then a file name that brings the path to the length.
        while (length - path.Length > 120)
        {
            path = Path.Combine(path, new string('d', 100));
        }

        var file = Path.Combine(path, new string('f', length - path.Length - 1 - ".bin".Length) + ".bin");
        Assert.Equal(length, file.Length);
        Directory.CreateDirectory(LongPath.Extended(path));
        File.WriteAllBytes(LongPath.Extended(file), new byte[10]);

        return file;
    }
}
