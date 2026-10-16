using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder the walk descends into that becomes a junction after its parent was listed, as a tool
/// relocating its cache onto another drive makes one. The parent's listing said it was a folder, so
/// the walk enters it, and what it must not do there is list the junction's target under the folder's
/// path: a scan would count a tree it never classified, and a removal would delete it.
/// </summary>
public sealed class DescendedLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheWalkListsNothingThroughAJunctionPutInPlaceOfAFolder()
    {
        var (root, folder, outside) = Fixture();
        var seen = new ConcurrentBag<string>();

        BoundedFileWalk.Visit<byte>(
            root,
            rootState: 0,
            new WalkTuning(1, WalkTuning.MinimumListingBuffer),
            FileNames.All,
            (directory, request, into) =>
            {
                SwapWhenListing(directory, folder, outside);
                return DirectoryQuery.List(directory, request, into);
            },
            (_, contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    seen.Add(entry.Name);
                    descend(entry, 0);
                }
            },
            static _ => { },
            TimeProvider.System,
            CancellationToken.None);

        Assert.True(LongPath.IsReparsePoint(folder), "the folder was never swapped for a junction");
        Assert.Contains("inside.bin", seen);
        Assert.DoesNotContain("outside.bin", seen);
    }

    /// <summary>
    /// §5.6: the junction's target survives a removal of the tree the junction was put into. Listed
    /// through, its file was deleted by a path under the folder the removal was asked to take.
    /// </summary>
    [Fact]
    public async Task ARemovalLeavesTheTargetOfAJunctionPutInPlaceOfAFolder()
    {
        var (root, folder, outside) = Fixture();
        var kept = Path.Combine(outside, "outside.bin");
        var swapped = false;
        var fs = new ListingHookFileSystem(WindowsFileSystem.Default, folder, () =>
        {
            SwapWhenListing(folder, folder, outside);
            swapped |= LongPath.IsReparsePoint(folder);
        });

        await DirectoryRemover.RemoveAsync(root, fileSystem: fs);

        // The removal takes the junction too, so whether it was ever put there is asked as it is listed.
        Assert.True(swapped, "the folder was never swapped for a junction");
        Assert.True(LongPath.FileExists(kept), "the junction's target lost its file");
        Assert.False(LongPath.DirectoryExists(root));
    }

    /// <summary>A root holding a file and an empty folder, and a folder outside it holding a file.</summary>
    private (string Root, string Folder, string Outside) Fixture()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(10, "root", "inside.bin");
        var folder = _temp.CreateDirectory("root", "folder");
        var outside = _temp.CreateDirectory("outside");
        _temp.CreateFile(10, "outside", "outside.bin");
        return (root, folder, outside);
    }

    /// <summary>Replace <paramref name="folder"/> with a junction to <paramref name="outside"/> when it is about to be listed.</summary>
    private static void SwapWhenListing(string directory, string folder, string outside)
    {
        if (LongPath.Display(directory).Equals(folder, StringComparison.OrdinalIgnoreCase) && !LongPath.IsReparsePoint(folder))
        {
            Directory.Delete(folder);
            Junction.ToDirectory(folder, outside);
        }
    }
}
