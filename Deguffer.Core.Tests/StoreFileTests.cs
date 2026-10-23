using System.Text;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every store under <c>%LOCALAPPDATA%\Deguffer</c> is replaced whole. A write straight into the file
/// truncates it first, so a process stopped part-way would leave a torn file and lose all it held.
/// </summary>
public class StoreFileTests
{
    [Fact]
    public void ReplacesTheFileAndCreatesItsFolder()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "Deguffer", "store.json");

        StoreFile.Replace(file, "first");
        StoreFile.Replace(file, "second");

        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal(["store.json"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(file)!).Select(Path.GetFileName));
    }

    /// <summary>
    /// A write stopped part-way, here by the disk refusing it. The file is still the last whole write,
    /// and the part written is not left beside it.
    /// </summary>
    [Fact]
    public void AWriteStoppedPartWayLeavesTheLastWholeFile()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "store.json");
        StoreFile.Replace(file, "{\"whole\":true}");

        Assert.Throws<IOException>(() => StoreFile.Replace(file, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("{\"tor"));
            throw new IOException("The disk is full.");
        }));

        Assert.Equal("{\"whole\":true}", File.ReadAllText(file));
        Assert.Equal(["store.json"], Directory.EnumerateFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }
}
