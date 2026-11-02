using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A fixture's policy reads the tree a test built under its own directory as it stands, and finds
/// nothing of the machine's anywhere else.
/// </summary>
public sealed class ConfinedFileSystemTests : IDisposable
{
    private readonly TempDirectory _inside = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    /// <summary>
    /// Inside the root, in either form <see cref="LongPath"/> produces, the disk answers: a folder that
    /// holds a refused location is seen to hold it, which the policy's refusal of that folder needs.
    /// </summary>
    [Fact]
    public void ReadsTheDiskInsideTheRoot()
    {
        var file = Path.Combine(_inside.CreateDirectory("scan", "games"), "game.iso");
        File.WriteAllText(file, "x");
        var disk = new ConfinedFileSystem(WindowsFileSystem.Default, _inside.Path);

        Assert.True(disk.MayExist(file));
        Assert.True(disk.MayExist(LongPath.Extended(file)));
        Assert.NotNull(disk.FinalPath(LongPath.Extended(file)));
        Assert.Equal(PathPresence.Present, disk.ProbeDirectory(Path.GetDirectoryName(file)!));
    }

    /// <summary>
    /// The temporary folder can be named by its 8.3 alias while the policy asks about the expanded
    /// path. Either spelling of the root confines the same tree.
    /// </summary>
    [Fact]
    public void ReadsTheDiskInsideTheRootUnderEitherSpelling()
    {
        var file = Path.Combine(_inside.Path, "notes.txt");
        File.WriteAllText(file, "x");
        var expanded = LongPath.Unaliased(file);
        var shortened = ShortPath.Of(file) ?? file;

        foreach (var root in (string[])[_inside.Path, LongPath.Unaliased(_inside.Path), ShortPath.Of(_inside.Path) ?? _inside.Path])
        {
            var disk = new ConfinedFileSystem(WindowsFileSystem.Default, root);

            Assert.True(disk.MayExist(expanded), root);
            Assert.True(disk.MayExist(shortened), root);
        }
    }

    /// <summary>
    /// The other half: outside the root nothing is there, even where the machine has something, so no
    /// answer can come from the machine running the suite. A folder whose name only starts like the
    /// root's is outside it.
    /// </summary>
    [Fact]
    public void FindsNothingOutsideTheRoot()
    {
        var elsewhere = Path.Combine(_outside.Path, "real.txt");
        File.WriteAllText(elsewhere, "x");
        var beside = _inside.Path + "-beside";
        Directory.CreateDirectory(beside);

        try
        {
            var disk = new ConfinedFileSystem(WindowsFileSystem.Default, _inside.Path);

            foreach (var path in (string[])[elsewhere, LongPath.Extended(elsewhere), beside, Path.GetPathRoot(_inside.Path)!])
            {
                Assert.False(disk.MayExist(path), path);
                Assert.Null(disk.FinalPath(path));
                Assert.Null(disk.TryGetAttributes(path));
                Assert.Equal(PathPresence.Absent, disk.ProbeEntry(path));
            }

            Assert.Throws<DirectoryNotFoundException>(() => disk.DeleteFile(elsewhere));
            Assert.True(File.Exists(elsewhere));
        }
        finally
        {
            Directory.Delete(beside);
        }
    }
}
