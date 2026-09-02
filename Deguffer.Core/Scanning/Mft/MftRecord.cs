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
/// <param name="Size">
/// Allocated and logical bytes of the unnamed <c>$DATA</c> stream, or null where the record does
/// not establish them — the attribute lives in an extension record, or describes only a later
/// extent, or is malformed. Null is not zero: a subtree holding one of these cannot be totalled,
/// and saying so is what sends the caller to the walk instead of reporting a cache short.
/// </param>
/// <param name="IsDirectory">Whether this entry contains other entries.</param>
/// <param name="IsReparsePoint">
/// Whether this entry is a junction or a link rather than the thing it names. Its target keeps its
/// own place in the table, so a link has no children here however much its path appears to hold.
/// </param>
public readonly record struct MftRecord(
    uint ParentRecordNumber,
    string Name,
    ScanSize? Size,
    bool IsDirectory,
    bool IsReparsePoint)
{
    /// <summary>
    /// The root directory always occupies record 5. Path resolution starts here, and the root is
    /// the one record whose parent is itself.
    /// </summary>
    public const uint RootRecordNumber = 5;

    /// <summary>
    /// NTFS reserves the first sixteen records of every volume for its own metadata files, and the
    /// count is fixed by the format rather than by a version or a formatting option.
    ///
    /// <para>Records 0 to 11 are the named ones — <c>$MFT</c>, <c>$MFTMirr</c>, <c>$LogFile</c>,
    /// <c>$Volume</c>, <c>$AttrDef</c>, the root, <c>$Bitmap</c>, <c>$Boot</c>, <c>$BadClus</c>,
    /// <c>$Secure</c>, <c>$UpCase</c> and <c>$Extend</c>. Records 12 to 15 are held back for future
    /// metadata: NTFS marks them in use and gives them neither a <c>$FILE_NAME</c> nor an
    /// <c>$ATTRIBUTE_LIST</c>, which is precisely the shape a reader has every reason to call
    /// corruption anywhere else in the table.</para>
    ///
    /// <para>None of the sixteen contributes to any directory total Deguffer measures. They are the
    /// volume's own bookkeeping, they hang off no user-visible directory, and their sizes are
    /// already outside what a cache subtree sums. So skipping one costs nothing, while refusing one
    /// costs the whole volume.</para>
    /// </summary>
    public const uint ReservedRecordCount = 16;
}
