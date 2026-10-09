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
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFileWhosePathIsTheLongestTheBinTakesIsTaken()
    {
        var file = FileOfLength(RecycleBinReach.LongestPath, "file");

        Assert.Null(RecycleBinReach.WhyNot(file));
    }

    [Fact]
    public void AFileWhosePathIsOneCharacterLongerIsRefused()
    {
        var file = FileOfLength(RecycleBinReach.LongestPath + 1, "file");

        Assert.Contains("260 characters long", RecycleBinReach.WhyNot(file));
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

        Assert.Contains("whose path is 260 characters long", RecycleBinReach.WhyNot(folder));
    }

    [Fact]
    public void AFolderWhosePathsAreAllShortEnoughIsTaken()
    {
        var folder = _temp.CreateDirectory("folder");
        FileOfLength(RecycleBinReach.LongestPath, Path.Combine("folder", "deep"));

        Assert.Null(RecycleBinReach.WhyNot(folder));
    }

    /// <summary>A link inside a folder is moved as a link, so what lies on its far side is not the folder's.</summary>
    [Fact]
    public void ALinkInsideAFolderIsNotLookedThrough()
    {
        var folder = _temp.CreateDirectory("folder");
        var target = _temp.CreateDirectory("target");
        FileOfLength(RecycleBinReach.LongestPath + 40, Path.Combine("target", "deep"));
        Junction.ToDirectory(Path.Combine(folder, "link"), target);

        Assert.Null(RecycleBinReach.WhyNot(folder));
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
        var bin = new ShellRecycleBin(path =>
        {
            handed.Add(path);
            File.Delete(LongPath.Extended(path));
            return new RecycleOutcome(Removed: true);
        });

        var outcome = bin.Recycle(file);

        Assert.False(outcome.Removed);
        Assert.Empty(handed);
        Assert.True(File.Exists(LongPath.Extended(file)));
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
