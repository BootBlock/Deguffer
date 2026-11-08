using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="StatByName"/>: a file's link count and sizes read by its name with no handle, which
/// must give the answer a handle opened to describe the file gives, or the hard-link figure would
/// depend on which route a file system left the scan.
/// </summary>
public sealed class StatByNameTests
{
    [Fact]
    public void ReadsTheLinkCountAndSizesAHandleReads()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile(10_000, "store", "blob.bin");
        HardLink.To(file, Path.Combine(temp.CreateDirectory("project"), "blob.bin"));

        var reading = StatByName.Read(0, LongPath.ObjectManagerForm(file), out var byName);

        using var handle = FileInformation.Open(LongPath.Extended(file), HandleUse.Describe);
        Assert.True(FileInformation.TryStandard(handle, out var byHandle));

        Assert.Equal(ByNameReading.Read, reading);
        Assert.Equal(2u, byName.NumberOfLinks);
        Assert.Equal(byHandle.NumberOfLinks, byName.NumberOfLinks);
        Assert.Equal(byHandle.EndOfFile, byName.EndOfFile);
        Assert.Equal(byHandle.AllocationSize, byName.AllocationSize);
    }

    /// <summary>
    /// A name the walk listed as a file and something replaced with a link before it was read. Read
    /// through the link, the target's bytes, outside the tree, would be counted as the tree's own.
    /// The read describes the link, as a handle opened to describe it does.
    /// </summary>
    [Fact]
    public void ReadsASymbolicLinkAsTheLinkRatherThanItsTarget()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile(1_000_000, "elsewhere", "big.bin");
        var link = Path.Combine(temp.CreateDirectory("store"), "blob.bin");
        SymbolicLink.ToFile(link, target);

        var reading = StatByName.Read(0, LongPath.ObjectManagerForm(link), out var info);

        Assert.Equal(ByNameReading.Read, reading);
        Assert.Equal(0, info.EndOfFile);
    }

    [Fact]
    public void AnAbsentFileIsGone()
    {
        using var temp = new TempDirectory();

        Assert.Equal(ByNameReading.Gone, StatByName.Read(0, LongPath.ObjectManagerForm(Path.Combine(temp.Path, "nowhere.bin")), out _));
        Assert.Equal(ByNameReading.Gone, StatByName.Read(0, LongPath.ObjectManagerForm(Path.Combine(temp.Path, "nowhere", "a.bin")), out _));
    }

    /// <summary>
    /// A name longer than a <c>UNICODE_STRING</c> can hold is refused, so a handle is opened on it,
    /// rather than its length wrapping and Windows being asked about the first part of the name.
    /// </summary>
    /// <remarks>
    /// The name is a real file's followed by exactly 32,768 more characters, so a length that wrapped
    /// would name that file and be answered, where the name as given is refused.
    /// </remarks>
    [Fact]
    public void ANameTooLongForTheNativeStringIsRefused()
    {
        using var temp = new TempDirectory();
        var file = LongPath.ObjectManagerForm(temp.CreateFile(4096, "a.bin"));
        var name = file + @"\" + new string('a', 32_767);

        Assert.Equal(ByNameReading.Read, StatByName.Read(0, file, out _));
        Assert.Equal(ByNameReading.Refused, StatByName.Read(0, name, out _));
    }

    /// <summary>
    /// A file named by its bare name relative to a handle on its folder, as the hard-link-aware walk
    /// names each file it listed, is read as it is by its full path.
    /// </summary>
    [Fact]
    public void ReadsAFileByItsNameRelativeToItsFolderAsByItsFullPath()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile(10_000, "store", "blob.bin");
        HardLink.To(file, Path.Combine(temp.CreateDirectory("project"), "blob.bin"));
        using var folder = Opened(Path.Combine(temp.Path, "store"));

        var relative = StatByName.Read(folder.Handle, "blob.bin", out var byName);
        var full = StatByName.Read(0, LongPath.ObjectManagerForm(file), out var byPath);

        Assert.Equal(ByNameReading.Read, relative);
        Assert.Equal(ByNameReading.Read, full);
        Assert.Equal(2u, byName.NumberOfLinks);
        Assert.Equal(byPath, byName);
    }

    [Fact]
    public void AFileAbsentFromTheFolderItIsReadRelativeToIsGone()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(4096, "store", "blob.bin");
        using var folder = Opened(Path.Combine(temp.Path, "store"));

        Assert.Equal(ByNameReading.Gone, StatByName.Read(folder.Handle, "nowhere.bin", out _));
    }

    /// <summary>
    /// The folder a file is read relative to is the one the walk listed, even where that folder was
    /// moved away and a junction to somewhere else put at its name since. The same file read by its
    /// full path is read through the junction, from outside the tree. (Windows refuses to move a folder
    /// above one held open, so the folder held is the one that can be swapped.)
    /// </summary>
    [Fact]
    public void AFileReadRelativeToItsFolderIsReadThereAfterTheFolderIsReplacedByAJunction()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(10_000, "store", "pkg", "blob.bin");
        temp.CreateFile(1, "elsewhere", "blob.bin");
        using var folder = Opened(Path.Combine(temp.Path, "store", "pkg"));

        Directory.Move(Path.Combine(temp.Path, "store", "pkg"), Path.Combine(temp.Path, "store", "moved"));
        Junction.ToDirectory(Path.Combine(temp.Path, "store", "pkg"), Path.Combine(temp.Path, "elsewhere"));

        Assert.Equal(ByNameReading.Read, StatByName.Read(folder.Handle, "blob.bin", out var relative));
        Assert.Equal(ByNameReading.Read, StatByName.Read(0, LongPath.ObjectManagerForm(Path.Combine(temp.Path, "store", "pkg", "blob.bin")), out var throughJunction));
        Assert.Equal(10_000, relative.EndOfFile);
        Assert.Equal(1, throughJunction.EndOfFile);
    }

    /// <summary>
    /// A file system that does not take the class is told apart from one that refused this file,
    /// because the first stops the walk asking and the second only sends this file to a handle.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0xC000_0003), nameof(ByNameReading.Unsupported))]
    [InlineData(unchecked((int)0xC000_0002), nameof(ByNameReading.Unsupported))]
    [InlineData(unchecked((int)0xC000_000D), nameof(ByNameReading.Unsupported))]
    [InlineData(unchecked((int)0xC000_0010), nameof(ByNameReading.Unsupported))]
    [InlineData(unchecked((int)0xC000_00BB), nameof(ByNameReading.Unsupported))]
    [InlineData(unchecked((int)0xC000_0022), nameof(ByNameReading.Refused))]
    [InlineData(unchecked((int)0xC000_0043), nameof(ByNameReading.Refused))]
    [InlineData(unchecked((int)0xC000_0056), nameof(ByNameReading.Refused))]
    public void SaysWhetherTheFileSystemOrTheFileRefused(int status, string expected)
    {
        Assert.Equal(expected, StatByName.Answer(status).ToString());
    }

    /// <summary><paramref name="folder"/> listed from the disk with its handle kept, as the hard-link-aware walk keeps it.</summary>
    private static ListedDirectoryHandle Opened(string folder)
    {
        using var memory = new ListingMemory(WalkTuning.MinimumListingBuffer);
        var held = new ListedDirectoryHandle();
        var request = new ListingRequest(memory, memory.Bytes, FollowsLink: true, FileNames.All) { Holder = held };

        Assert.Equal(ListingEnd.Listed, DirectoryQuery.List(LongPath.Extended(folder), request, []));
        Assert.NotEqual(0, held.Handle);

        return held;
    }
}
