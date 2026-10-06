namespace Deguffer.Core.Scanning;

/// <summary>
/// Why a file occupies less of the disk than its length, where something says so. A folder's value
/// is every kind found at or below it.
///
/// <para>Carried so that a picture drawn by space on disk can explain itself. A 200 GB file drawn
/// as nothing reads as a fault in the scan until the reader is told it is in the cloud.</para>
/// </summary>
[Flags]
public enum FileStorage : byte
{
    /// <summary>Nothing says the file is stored other than as its length.</summary>
    Plain = 0,

    /// <summary>
    /// A cloud file whose content is not all on this device: OneDrive's "online only", or one only
    /// partly downloaded. Freeing it frees what it occupies here, which is little or nothing.
    /// </summary>
    CloudOnly = 1,

    /// <summary>Compressed by NTFS, or by Windows itself (CompactOS).</summary>
    Compressed = 2,

    /// <summary>A sparse file, whose unwritten ranges occupy nothing: a virtual disk or a database.</summary>
    Sparse = 4,
}

/// <summary>
/// Reads <see cref="FileStorage"/> from a file's attributes, the one source both scan routes have:
/// the walk is handed them by the listing, and the file table keeps the same bits in
/// <c>$STANDARD_INFORMATION</c>.
///
/// <para>CompactOS is the exception, and it is why <see cref="Of"/> cannot be the whole answer. Its
/// filter hides the file's reparse point and its compressed stream from every listing and every
/// attribute read, so a listing cannot tell such a file from a plain one. Only the file table sees
/// it, by the stream the filter hides.</para>
/// </summary>
public static class StorageAttributes
{
    /// <summary>
    /// <c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c> and <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>, which
    /// <see cref="FileAttributes"/> has no names for. A cloud provider sets one of them on a file whose
    /// content has to be fetched before it can be read.
    /// </summary>
    private const FileAttributes Recall = (FileAttributes)0x0004_0000 | (FileAttributes)0x0040_0000;

    private const FileAttributes CloudOnly = FileAttributes.Offline | Recall;

    /// <summary>
    /// Every attribute that can make a file occupy something other than its length. The reparse
    /// point is among them for the files Windows does not disguise, such as a deduplicated file.
    /// </summary>
    private const FileAttributes MayDiffer =
        CloudOnly | FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint;

    /// <summary>
    /// What the attributes say about how a file is stored.
    ///
    /// <para>A cloud file is never also called sparse. Its provider makes it sparse to empty it, so
    /// the one cause is the cloud, and it is the only one the walk can see: Windows hides the sparse
    /// bit from a listing along with the placeholder's reparse point, and the file table keeps both.
    /// Naming the one cause is what lets the two routes describe the file alike.</para>
    /// </summary>
    public static FileStorage Of(FileAttributes attributes)
    {
        if ((attributes & CloudOnly) != 0)
        {
            return FileStorage.CloudOnly;
        }

        var storage = FileStorage.Plain;

        if (attributes.HasFlag(FileAttributes.Compressed))
        {
            storage |= FileStorage.Compressed;
        }

        if (attributes.HasFlag(FileAttributes.SparseFile))
        {
            storage |= FileStorage.Sparse;
        }

        return storage;
    }

    /// <summary>
    /// Whether a file's length may not be what it occupies, so the walk has to ask the file system.
    /// Every other file is taken at its length, which is what keeps the walk to one listing per
    /// directory.
    /// </summary>
    public static bool MayDifferFromLength(FileAttributes attributes) => (attributes & MayDiffer) != 0;
}
