using Deguffer.Core.Safety;
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

        var reading = StatByName.Read(LongPath.Extended(file), out var byName);

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

        var reading = StatByName.Read(LongPath.Extended(link), out var info);

        Assert.Equal(ByNameReading.Read, reading);
        Assert.Equal(0, info.EndOfFile);
    }

    [Fact]
    public void AnAbsentFileIsGone()
    {
        using var temp = new TempDirectory();

        Assert.Equal(ByNameReading.Gone, StatByName.Read(LongPath.Extended(Path.Combine(temp.Path, "nowhere.bin")), out _));
        Assert.Equal(ByNameReading.Gone, StatByName.Read(LongPath.Extended(Path.Combine(temp.Path, "nowhere", "a.bin")), out _));
    }

    /// <summary>
    /// A name longer than a <c>UNICODE_STRING</c> can hold is refused, so a handle is opened on it,
    /// rather than its length wrapping and Windows being asked about the first part of the name.
    /// </summary>
    [Fact]
    public void ANameTooLongForTheNativeStringIsRefused()
    {
        var name = @"\\?\C:\" + new string('a', 40_000);

        Assert.Equal(ByNameReading.Refused, StatByName.Read(name, out _));
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
}
