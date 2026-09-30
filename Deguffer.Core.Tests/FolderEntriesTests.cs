using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The file-and-folder listing providers read their recognised files through, and the two facts it
/// keeps: a folder that is not there holds nothing, and a folder that refused to be listed is not
/// empty. Both are answered without throwing, because providers ask on every scan and most of the
/// places they ask about are not there.
/// </summary>
public sealed class FolderEntriesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ListsFilesAndFolders()
    {
        var folder = _temp.CreateDirectory("folder");
        _temp.CreateDirectory("folder", "child");
        _temp.CreateFile(8, "folder", "file.bin");

        IReadOnlyList<FileSystemInfo>? entries = null;
        var thrown = ThrownExceptions.During(() => entries = FolderEntries.Of(folder));

        Assert.Equal(["child", "file.bin"], entries!.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Empty(thrown);
    }

    [Fact]
    public void AFolderThatIsNotThereIsEmpty()
    {
        IReadOnlyList<FileSystemInfo>? entries = null;
        var thrown = ThrownExceptions.During(() => entries = FolderEntries.Of(Path.Combine(_temp.Path, "never-created")));

        Assert.NotNull(entries);
        Assert.Empty(entries);
        Assert.Empty(thrown);
    }

    /// <summary>
    /// Null, not empty, and nothing from before the refusal: half a listing describes a folder nobody
    /// fully read.
    /// </summary>
    [Fact]
    public void AFolderThatWillNotBeListedIsNull()
    {
        var folder = _temp.CreateDirectory("refused");
        _temp.CreateFile(8, "refused", "inside.bin");

        using var denied = new DeniedDirectory(folder);

        IReadOnlyList<FileSystemInfo>? entries = [];
        var thrown = ThrownExceptions.During(() => entries = FolderEntries.Of(folder));

        Assert.Null(entries);
        Assert.Empty(thrown);
    }
}
