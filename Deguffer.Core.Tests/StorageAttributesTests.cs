using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a file's attributes say about how it is stored, which both scan routes read the same way.
/// </summary>
public class StorageAttributesTests
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;

    [Theory]
    [InlineData(FileAttributes.Archive, FileStorage.Plain)]
    [InlineData(FileAttributes.Archive | FileAttributes.Compressed, FileStorage.Compressed)]
    [InlineData(FileAttributes.Archive | FileAttributes.SparseFile, FileStorage.Sparse)]
    [InlineData(FileAttributes.Compressed | FileAttributes.SparseFile, FileStorage.Compressed | FileStorage.Sparse)]
    [InlineData(FileAttributes.Archive | RecallOnDataAccess, FileStorage.CloudOnly)]
    [InlineData(FileAttributes.Archive | RecallOnOpen, FileStorage.CloudOnly)]
    [InlineData(FileAttributes.Archive | FileAttributes.Offline, FileStorage.CloudOnly)]
    public void ReadsHowAFileIsStored(FileAttributes attributes, FileStorage expected) =>
        Assert.Equal(expected, StorageAttributes.Of(attributes));

    /// <summary>
    /// A placeholder is sparse only because its provider emptied it, and Windows hides that bit from
    /// a listing while the file table keeps it. Naming the cloud alone is what lets the two routes
    /// describe it alike.
    /// </summary>
    [Fact]
    public void CallsACloudFileNothingButACloudFile()
    {
        var exposed = FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.ReparsePoint
            | FileAttributes.Offline | RecallOnDataAccess;
        var listed = FileAttributes.Archive | RecallOnDataAccess;

        Assert.Equal(FileStorage.CloudOnly, StorageAttributes.Of(exposed));
        Assert.Equal(StorageAttributes.Of(listed), StorageAttributes.Of(exposed));
    }

    /// <summary>
    /// Only a file whose attributes allow its length to differ from what it occupies is worth asking
    /// about. A reparse point counts, because a deduplicated file carries one and nothing else.
    /// </summary>
    [Theory]
    [InlineData(FileAttributes.Archive, false)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System, false)]
    [InlineData(FileAttributes.Compressed, true)]
    [InlineData(FileAttributes.SparseFile, true)]
    [InlineData(FileAttributes.ReparsePoint, true)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData(RecallOnDataAccess, true)]
    [InlineData(RecallOnOpen, true)]
    public void AsksAboutOnlyAFileThatMayNotOccupyItsLength(FileAttributes attributes, bool asks) =>
        Assert.Equal(asks, StorageAttributes.MayDifferFromLength(attributes));
}
