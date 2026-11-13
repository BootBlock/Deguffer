using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The paths a removal's walk builds and keeps. A removal holds a path for every folder of its tree
/// until it is over, and a bounded removal (a <c>%TEMP%</c> of half a million entries) asks its bounds
/// about every entry, so a path built twice, or built only to be looked up, is memory and garbage made
/// once per folder or per entry (G4).
/// </summary>
public sealed class RemovalWalkPathTests
{
    // As long as a package cache's folders run, so a path built per entry is a cost the bound can see.
    private const string Root = @"Q:\Users\testuser\AppData\Local\a-tool-with-a-long-name\caches\packages-by-version";

    private const int Folders = 20;

    private const int FilesInEach = 200;

    private static readonly WalkTuning OneWorker = new(1, WalkTuning.MinimumListingBuffer);

    /// <summary>
    /// A folder below the root keeps one string for where it was found and how it was listed: the walk
    /// lists it by the path the folder was made with. Two copies of each folder's path would be held for
    /// the whole removal, about 26 MB per 100,000 folders.
    /// </summary>
    [Fact]
    public void AFolderKeepsTheOneStringItWasFoundAndListedBy()
    {
        var fs = Tree();

        var folders = Gather(fs, RemovalBounds.None).InWalkOrder();

        Assert.Equal(Folders + 1, folders.Count);
        Assert.All(folders, folder => Assert.Same(folder.FoundAs, folder.Path));
        Assert.All(folders, folder => Assert.Contains(fs.Listed, listed => ReferenceEquals(listed, folder.Path)));
    }

    /// <summary>
    /// A bounded removal whose bounds name nothing in the tree allocates no more for each entry than one
    /// that holds nothing back. The bounds are asked once per folder, by the string its listing already
    /// has, and a folder holding no bound path builds no entry's path to look up. Asked per entry, each
    /// built a path of about 250 bytes and dropped it.
    /// </summary>
    [Fact]
    public void ABoundThatNamesNothingInAFolderBuildsNoPathForItsEntries()
    {
        var fs = Tree();
        var bounds = new RemovalBounds(
            KeepRoot: true,
            [Path.Combine(Root, "folder-00", "not-here")],
            [Path.Combine(Root, "elsewhere", "another-rows")],
            [@"Q:\Users\testuser\Documents\sketch.psd"]);

        var unbounded = LeastPerEntry(fs, RemovalBounds.None);
        var bounded = LeastPerEntry(fs, bounds);

        // The only bound folder in the tree is folder-00, whose 200 files are looked up by name.
        Assert.True(
            bounded - unbounded < 8,
            $"A bounded walk allocated {bounded:N0} bytes an entry, and an unbounded one {unbounded:N0}.");
    }

    /// <summary>
    /// A bound path directly below a volume's top is held back. The listing spells that folder
    /// <c>\\?\Q:\</c>, with its separator, and the folder cut from the bound path is <c>\\?\Q:</c>, so a
    /// lookup that matched the spellings exactly would delete every one of them. Each is reported in the
    /// <c>\\?\</c> form, and none is descended into or handed to a delete (§5.2, §5.6).
    /// </summary>
    [Fact]
    public void HoldsBackABoundPathDirectlyBelowAVolumesTop()
    {
        var fs = new ListedTreeFileSystem(@"Q:\")
            .AddFile(@"Q:\session\inside.bin")
            .AddFile(@"Q:\notes\draft.txt")
            .AddFile(@"Q:\another-rows.bin")
            .AddFile(@"Q:\loose.bin");

        var bounds = new RemovalBounds(
            KeepRoot: true,
            [@"Q:\session"],
            [@"\\?\Q:\another-rows.bin"],
            [@"Q:\NOTES\"]);

        var root = RemovalWalk.Gather(LongPath.Extended(@"Q:\"), MinimumAge.Off, bounds, fs, OneWorker, default);

        Assert.Equal([@"\\?\Q:\session"], root.Spared);
        Assert.Equal([@"\\?\Q:\notes"], root.UserWork);
        Assert.Equal([@"\\?\Q:\loose.bin"], root.Files.Select(file => file.FullName));
        Assert.Single(root.InWalkOrder());
        Assert.Equal([@"\\?\Q:\"], fs.Listed);
    }

    /// <summary>
    /// A bound path written with a trailing separator holds back the entry it names, at any depth.
    /// </summary>
    [Fact]
    public void HoldsBackABoundPathWrittenWithATrailingSeparator()
    {
        var held = Path.Combine(Root, "folder-03");
        var fs = Tree();

        var root = Gather(fs, new RemovalBounds(KeepRoot: true, [held + Path.DirectorySeparatorChar]));

        Assert.Equal([LongPath.Extended(held)], root.InWalkOrder().SelectMany(folder => folder.Spared));
        Assert.DoesNotContain(fs.Listed, listed => listed.Equals(LongPath.Extended(held), StringComparison.OrdinalIgnoreCase));
        Assert.Equal((Folders - 1) * FilesInEach, root.InWalkOrder().Sum(folder => folder.Files.Count));
    }

    /// <summary>
    /// The least a gather of the tree allocated for each entry, over three gathers, on the one worker that
    /// is the calling thread, so what this thread allocated is what the walk did.
    /// </summary>
    private static long LeastPerEntry(ListedTreeFileSystem fs, RemovalBounds bounds)
    {
        // Once first, so what is counted is the walk rather than the first call's compilation.
        Gather(fs, bounds);

        var least = long.MaxValue;

        for (var gather = 0; gather < 3; gather++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Gather(fs, bounds);
            least = Math.Min(least, (GC.GetAllocatedBytesForCurrentThread() - before) / (Folders * (FilesInEach + 1)));
        }

        return least;
    }

    private static RemovalWalk.Folder Gather(ListedTreeFileSystem fs, RemovalBounds bounds) =>
        RemovalWalk.Gather(LongPath.Extended(Root), MinimumAge.Off, bounds, fs, OneWorker, default);

    private static ListedTreeFileSystem Tree()
    {
        var fs = new ListedTreeFileSystem(Root);

        for (var folder = 0; folder < Folders; folder++)
        {
            for (var file = 0; file < FilesInEach; file++)
            {
                fs.AddFile(Path.Combine(Root, $"folder-{folder:D2}", $"file-{file:D3}.bin"));
            }
        }

        return fs;
    }
}
