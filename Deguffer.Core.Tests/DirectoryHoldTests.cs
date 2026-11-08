using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="DirectoryHold.DuringCallback"/>: a walk that keeps each directory it lists open while its
/// callback reads the contents, so a file can be read by its bare name relative to the directory. The
/// handle must name the directory listed and no other, and must not outlive the callback.
/// </summary>
public sealed class DirectoryHoldTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EachCallbackIsHandedAHandleOnTheDirectoryItsContentsCameFrom()
    {
        _temp.CreateFile(1, "root", "a", "b", "file.bin");
        var opened = new ConcurrentDictionary<string, string?>();

        Walk(
            DirectoryHold.DuringCallback,
            DirectoryQuery.List,
            contents => opened[Path.GetFileName(contents.Directory)] = FolderOf(contents.Handle),
            _ => { });

        Assert.Equal(["a", "b", "root"], opened.Keys.Order());
        Assert.All(opened, folder => Assert.Equal(folder.Key, Path.GetFileName(folder.Value)));
    }

    [Fact]
    public void AWalkThatDoesNotAskIsHandedNoHandleAndHoldsNothing()
    {
        _temp.CreateFile(1, "root", "a", "file.bin");
        var handles = new ConcurrentBag<nint>();
        var holders = new ConcurrentBag<ListedDirectoryHandle?>();

        Walk(
            DirectoryHold.None,
            (directory, request, into) =>
            {
                holders.Add(request.Holder);
                return DirectoryQuery.List(directory, request, into);
            },
            contents => handles.Add(contents.Handle),
            _ => { });

        Assert.Equal([0, 0], handles);
        Assert.Equal([null, null], holders);
    }

    /// <summary>
    /// The walk reports progress once the first directory's callback is over, before the worker lists
    /// anything else, and the handle is closed by then rather than held until the next listing.
    /// </summary>
    [Fact]
    public void TheHandleIsClosedAsSoonAsTheCallbackReturns()
    {
        _temp.CreateFile(1, "root", "file.bin");
        var holder = new ListedDirectoryHandle[1];
        var during = new List<nint>();
        var after = new List<nint>();

        Walk(
            DirectoryHold.DuringCallback,
            Capturing(holder),
            contents => during.Add(holder[0].Handle),
            _ => after.Add(holder[0].Handle));

        Assert.NotEqual(0, Assert.Single(during));
        Assert.NotEmpty(after);
        Assert.All(after, handle => Assert.Equal(0, handle));
    }

    [Fact]
    public void TheHandleIsClosedWhenTheCallbackThrows()
    {
        _temp.CreateFile(1, "root", "file.bin");
        var holder = new ListedDirectoryHandle[1];
        nint during = 0;

        Assert.Throws<InvalidOperationException>(() => Walk(
            DirectoryHold.DuringCallback,
            Capturing(holder),
            contents =>
            {
                during = holder[0].Handle;
                throw new InvalidOperationException("The callback failed.");
            },
            _ => { }));

        Assert.NotEqual(0, during);
        Assert.Equal(0, holder[0].Handle);
    }

    /// <summary>
    /// A directory that could not be opened has no handle, never the one the worker held for the
    /// directory it listed before: a file read relative to that would be read in the wrong directory.
    /// </summary>
    [Fact]
    public void ADirectoryThatCouldNotBeOpenedIsHandedNoHandleEvenAfterOneThatWas()
    {
        var present = LongPath.Extended(_temp.CreateDirectory("present"));
        var absent = LongPath.Extended(Path.Combine(_temp.Path, "absent"));
        using var listing = new WalkListing(
            new ListingBuffer(WalkTuning.MinimumListingBuffer), DirectoryQuery.List, FileNames.All, DirectoryHold.DuringCallback);

        Assert.NotEqual(0, listing.Read(present, followsLink: true).Handle);

        var gone = listing.Read(absent, followsLink: true);

        Assert.Equal(PathPresence.Absent, gone.Presence);
        Assert.Equal(0, gone.Handle);
    }

    /// <summary>
    /// A test's listing opens nothing, so its contents carry no handle, and a file it lists is read by
    /// its full path instead, in the object manager's form of §6.3's extended path.
    /// </summary>
    [Fact]
    public void AFileFromAListingWithNoHandleIsReadByItsFullPath()
    {
        var folder = LongPath.Extended(_temp.CreateDirectory("store"));
        var file = new WalkEntry(folder, "blob.bin", FileAttributes.Normal, 4096, DateTime.UtcNow, DateTime.UtcNow);
        var asked = new List<(nint Directory, string Name)>();
        var files = new FileInformation(
            FileInformation.Open,
            FileInformation.ReadIdentity,
            readByName: (nint directory, ReadOnlySpan<char> name, out FileInformation.FileStandardInfo info) =>
            {
                asked.Add((directory, name.ToString()));
                info = new FileInformation.FileStandardInfo { EndOfFile = 4096, NumberOfLinks = 1 };
                return ByNameReading.Read;
            });

        Assert.Equal(4096, files.Standard(0, file, new ByNameRoute())?.EndOfFile);
        Assert.Equal([(0, LongPath.ObjectManagerForm(Path.Combine(folder, "blob.bin")))], asked);
    }

    /// <summary>The temporary root walked on one thread, so each listing follows the last callback's end.</summary>
    private void Walk(DirectoryHold hold, ListDirectory list, Action<DirectoryContents> onDirectory, Action<bool> onProgress) =>
        BoundedFileWalk.Visit<byte>(
            [(Path.Combine(_temp.Path, "root"), 0)],
            new WalkTuning(1, WalkTuning.MinimumListingBuffer),
            FileNames.All,
            hold,
            list,
            (_, contents, descend) =>
            {
                onDirectory(contents);

                foreach (var entry in contents.Entries)
                {
                    if (entry.IsDirectory)
                    {
                        descend(entry, 0);
                    }
                }
            },
            onProgress,
            TimeProvider.System,
            CancellationToken.None);

    /// <summary>The listing from the disk, keeping the worker's holder in <paramref name="holder"/>.</summary>
    private static ListDirectory Capturing(ListedDirectoryHandle[] holder) => (directory, request, into) =>
    {
        holder[0] = request.Holder!;
        return DirectoryQuery.List(directory, request, into);
    };

    /// <summary>Where <paramref name="handle"/> is open on, asked without taking it over.</summary>
    private static string? FolderOf(nint handle)
    {
        using var borrowed = new SafeFileHandle(handle, ownsHandle: false);

        return FileInformation.FinalPathOf(borrowed);
    }
}
