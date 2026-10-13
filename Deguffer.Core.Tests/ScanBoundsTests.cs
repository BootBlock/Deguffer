using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A measurement taken on the terms a removal leaves things standing on, by the two walks. The
/// table's half is <see cref="MftVolumeIndexLeftOutTests"/>.
///
/// <para>Each figure is held to the one it replaced: the tree measured whole, less each entry left
/// out measured again on its own. That subtraction was right and walked every such entry twice, so
/// the only thing allowed to change is the cost.</para>
///
/// <para>"Not entered" is proved by an Outlook data file inside the entry left out. Every walk names
/// each store it meets (§9), so a store that is not named is one the walk never listed.</para>
/// </summary>
public sealed class ScanBoundsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ScanSize Measure(IDirectoryScanner scanner, string path, ScanBounds bounds = default) =>
        Result(scanner, path, bounds).Size;

    private static ScanResult Result(IDirectoryScanner scanner, string path, ScanBounds bounds = default) =>
        scanner.MeasureAsync(path, MinimumAge.Off, bounds).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// A temporary folder as the row meets it: an entry a program holds, one another row owns several
    /// folders down, a file another row owns, and what this row takes around them.
    /// </summary>
    private (string Root, string[] LeftOut) TemporaryFolder()
    {
        var root = _temp.CreateDirectory("temp");
        _temp.CreateFile(1000, "temp", "abandoned.tmp");
        _temp.CreateFile(3000, "temp", "Adobe", "beside.tmp");
        _temp.CreateFile(5000, "temp", "live-session", "working.txt");
        _temp.CreateFile(7000, "temp", "live-session", "deeper", "state.bin");
        _temp.CreateFile(11000, "temp", "Adobe", "Disk Cache", "frame.bin");
        _temp.CreateFile(13000, "temp", "installer.msi");

        return (root,
        [
            Path.Combine(root, "live-session"),
            Path.Combine(root, "Adobe", "Disk Cache"),
            Path.Combine(root, "installer.msi"),
        ]);
    }

    [Fact]
    public void TheFigureEqualsTheTreeLessEachEntryMeasuredOnItsOwn()
    {
        var (root, leftOut) = TemporaryFolder();
        var scanner = ParallelEnumerationScanner.Default;

        var whole = Measure(scanner, root);
        var subtracted = leftOut.Aggregate(whole, (total, path) => total - Measure(scanner, path));

        var bounded = Measure(scanner, root, new ScanBounds(KeepRoot: false, leftOut));

        Assert.Equal(1000 + 3000, bounded.Logical);
        Assert.Equal(subtracted.Logical, bounded.Logical);
        Assert.Equal(subtracted.Allocated, bounded.Allocated);
    }

    /// <summary>
    /// The same comparison on entries, where the old figure was right only by the adjustment the
    /// planner made for the folder's own entry. Here the folder is kept standing by the measurement,
    /// so the count is what the clean takes: two files, and nothing else, because every folder left
    /// holds something that stays.
    /// </summary>
    [Fact]
    public void CountsWhatTheCleanTakesAndNotTheFolderItEmptiesInPlace()
    {
        var (root, leftOut) = TemporaryFolder();

        var bounded = Measure(ParallelEnumerationScanner.Default, root, new ScanBounds(KeepRoot: true, leftOut));

        Assert.Equal(2, bounded.Entries);
    }

    /// <summary>
    /// A folder emptied in place with nothing left out: everything inside it is counted, and the folder
    /// itself is not, whether or not anything inside it stays.
    /// </summary>
    [Fact]
    public void KeepingTheRootLeavesOnlyItsOwnEntryOut()
    {
        var root = _temp.CreateDirectory("bin");
        _temp.CreateFile(1000, "bin", "a.tmp");
        _temp.CreateFile(2000, "bin", "folder", "b.tmp");

        var whole = Measure(ParallelEnumerationScanner.Default, root);
        var contents = Measure(ParallelEnumerationScanner.Default, root, ScanBounds.Contents);

        Assert.Equal(4, whole.Entries);
        Assert.Equal(3, contents.Entries);
        Assert.Equal(whole.Logical, contents.Logical);
    }

    [Fact]
    public void NeverEntersAnEntryLeftOut()
    {
        var (root, leftOut) = TemporaryFolder();
        _temp.CreateFile(100, "temp", "live-session", "deeper", "archive.pst");
        _temp.CreateFile(100, "temp", "Adobe", "Disk Cache", "archive.pst");

        var whole = Result(ParallelEnumerationScanner.Default, root);
        var bounded = Result(ParallelEnumerationScanner.Default, root, new ScanBounds(KeepRoot: true, leftOut));

        Assert.Equal(2, whole.MailStores.Count);
        Assert.Empty(bounded.MailStores);
    }

    /// <summary>
    /// The link-aware walk leaves the same entries out, unread and unentered, so a store measured
    /// through it with entries held back is not counted for them.
    /// </summary>
    [Fact]
    public void TheLinkAwareWalkLeavesTheSameEntriesOutWithoutEnteringThem()
    {
        var (root, leftOut) = TemporaryFolder();
        _temp.CreateFile(100, "temp", "live-session", "archive.pst");

        var whole = Result(HardLinkAwareScanner.Default, root);
        var bounded = Result(HardLinkAwareScanner.Default, root, new ScanBounds(KeepRoot: true, leftOut));

        Assert.Equal(1000 + 3000 + 5000 + 7000 + 11000 + 13000, whole.Size.Logical);
        Assert.Equal(1000 + 3000, bounded.Size.Logical);
        Assert.Single(whole.MailStores);
        Assert.Empty(bounded.MailStores);
    }

    /// <summary>
    /// An entry is matched as the place it names. A path a program reported in the extended form, in
    /// another case, or through an 8.3 alias is the same entry, and read as another it would be
    /// counted while the clean leaves it.
    /// </summary>
    [Fact]
    public void MatchesAnEntryHoweverItsPathIsSpelled()
    {
        var root = _temp.CreateDirectory("Temporary Folder");
        _temp.CreateFile(1000, "Temporary Folder", "abandoned.tmp");
        var session = _temp.CreateDirectory("Temporary Folder", "Session-Folder-Long");
        _temp.CreateFile(5000, "Temporary Folder", "Session-Folder-Long", "working.txt");
        var installer = _temp.CreateFile(7000, "Temporary Folder", "Installer.MSI");

        string[] spellings =
        [
            LongPath.Extended(session).ToUpperInvariant(),
            ShortPath.Of(installer) ?? installer.ToLowerInvariant(),
        ];

        var bounded = Measure(ParallelEnumerationScanner.Default, root, new ScanBounds(KeepRoot: true, spellings));

        Assert.Equal(1000, bounded.Logical);
    }

    /// <summary>
    /// The measured folder reached through its alias, and the entries named in full. The keys are
    /// spelled from the root as the walk lists it, so the two spellings still meet.
    /// </summary>
    [Fact]
    public void MatchesEntriesBelowARootReachedThroughAnotherSpelling()
    {
        var root = _temp.CreateDirectory("Temporary Folder");
        _temp.CreateFile(1000, "Temporary Folder", "abandoned.tmp");
        var session = _temp.CreateDirectory("Temporary Folder", "Session-Folder-Long");
        _temp.CreateFile(5000, "Temporary Folder", "Session-Folder-Long", "working.txt");

        var asReached = ShortPath.Of(root) ?? LongPath.Extended(root);

        var bounded = Measure(ParallelEnumerationScanner.Default, asReached, new ScanBounds(KeepRoot: true, [session]));

        Assert.Equal(1000, bounded.Logical);
    }

    /// <summary>
    /// Only what is below the measured folder is left out of it. The folder itself, a sibling whose
    /// name it begins with, and anything elsewhere are not, so a stray path in the list cannot zero
    /// the figure or reach outside it.
    /// </summary>
    [Fact]
    public void IgnoresAPathThatIsNotBelowTheMeasuredFolder()
    {
        var root = _temp.CreateDirectory("temp");
        _temp.CreateFile(1000, "temp", "abandoned.tmp");
        var sibling = _temp.CreateDirectory("temp-other");

        var bounded = Measure(
            ParallelEnumerationScanner.Default,
            root,
            new ScanBounds(KeepRoot: true, [root, sibling, Path.Combine(sibling, "abandoned.tmp")]));

        Assert.Equal(1000, bounded.Logical);
        Assert.Equal(1, bounded.Entries);
    }
}
