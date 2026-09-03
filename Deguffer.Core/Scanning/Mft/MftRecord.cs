namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One parsed MFT entry, reduced to what a scan needs: where it sits in the tree, what it is
/// called, how much space it occupies, and when it was made and last written.
///
/// Deliberately not a general-purpose NTFS record. Security descriptors, named streams and reparse
/// data are all present on disk and all irrelevant here; carrying them would cost memory per record
/// across a volume-wide index for no benefit.
///
/// The two timestamps are the exception, and they earn their place because they cost nothing to
/// take: <c>$STANDARD_INFORMATION</c> is materialised in the same record the parser is already
/// walking, so the whole volume is dated by the pass it is already making. This struct is built and
/// discarded one record at a time, so the sixteen bytes are never held in bulk here — what the
/// picture keeps is <see cref="Exploring.ExploreTimestamp"/>, which is a quarter of the size.
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
/// <param name="CreatedFileTime">
/// When the entry was made, as NTFS stores it: 100-nanosecond intervals since the start of 1601.
/// Zero where the record does not establish it, which is the value NTFS itself writes for a
/// timestamp it never set.
/// </param>
/// <param name="LastWrittenFileTime">
/// When the entry's contents were last altered, in the same units.
///
/// <para>Read from <c>$STANDARD_INFORMATION</c> and deliberately not from the copy in
/// <c>$FILE_NAME</c>. NTFS refreshes that second copy when the name changes rather than when the
/// file does, so a file written every day since it was renamed once reports the date of the
/// rename — the same trap the reparse-point flag beside the name sets, and the same answer.</para>
/// </param>
public readonly record struct MftRecord(
    uint ParentRecordNumber,
    string Name,
    ScanSize? Size,
    bool IsDirectory,
    bool IsReparsePoint,
    long CreatedFileTime,
    long LastWrittenFileTime)
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

    /// <summary>
    /// The first of the reserved records NTFS leaves nameless. 0 to 11 are the named metadata files
    /// and parse like any other record; 12 to 15 are held back for future use, marked in use and
    /// given neither a <c>$FILE_NAME</c> nor an <c>$ATTRIBUTE_LIST</c>.
    ///
    /// <para>The distinction is what keeps the builder's tolerance as narrow as the fact behind it.
    /// A record it cannot read anywhere else in the table is damage and takes the volume, and that
    /// has to stay true of a torn <c>$MFT</c> or <c>$LogFile</c> too — those are real files with
    /// real sizes, and a build that skipped one would answer short for the volume root.</para>
    /// </summary>
    public const uint FirstUnnamedReservedRecord = 12;
}
