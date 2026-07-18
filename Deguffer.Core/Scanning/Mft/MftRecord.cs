namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One parsed MFT entry, reduced to what a size scan needs: where it sits in the tree, what it is
/// called, and how much space it occupies.
///
/// Deliberately not a general-purpose NTFS record. Timestamps, security descriptors, streams and
/// reparse data are all present on disk and all irrelevant here; carrying them would cost memory
/// per record across a volume-wide index for no benefit.
/// </summary>
/// <param name="ParentRecordNumber">Record number of the containing directory.</param>
/// <param name="Name">The file or directory name, without any path.</param>
/// <param name="Size">Allocated and logical bytes of the unnamed <c>$DATA</c> stream.</param>
/// <param name="IsDirectory">Whether this entry contains other entries.</param>
public readonly record struct MftRecord(
    uint ParentRecordNumber,
    string Name,
    ScanSize Size,
    bool IsDirectory)
{
    /// <summary>
    /// The root directory always occupies record 5. Path resolution starts here, and the root is
    /// the one record whose parent is itself.
    /// </summary>
    public const uint RootRecordNumber = 5;
}
