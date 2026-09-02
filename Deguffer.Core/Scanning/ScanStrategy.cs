namespace Deguffer.Core.Scanning;

/// <summary>Which of §5.5's two measurement routes produced a number.</summary>
public enum ScanStrategy
{
    /// <summary>The MFT was read directly — one pass per volume, then every query is a lookup.</summary>
    MasterFileTable,

    /// <summary>
    /// §5.5's bounded parallel directory enumeration. Correct, and too slow to be the scanner on
    /// its own.
    ///
    /// <para>Usually it carries a <see cref="FallbackReason"/>, because usually it was reached by
    /// falling back. The exception is <see cref="HardLinkAwareScanner"/>, which walks by choice:
    /// a hard-link count has to be read per file, the file table cannot be read unelevated, and a
    /// figure that existed only under elevation would disagree with the ordinary run's. Nothing was
    /// fallen back from there, so nothing is offered — see <see cref="ScanResult.ByChoice"/>.</para>
    /// </summary>
    ParallelEnumeration,

    /// <summary>
    /// One file, read directly. Neither of §5.5's routes applies: there is no tree to walk, and
    /// nothing for the index to save — which is just as well, since the index cannot resolve a file
    /// path at all (only directories carry names in it, deliberately, because naming every file on a
    /// volume would cost a string per record).
    ///
    /// It matters that this is not <see cref="ParallelEnumeration"/>. The walk always carries a
    /// <see cref="FallbackReason"/> and the user is shown a sentence about a slow scan; a single
    /// <c>stat</c> is not slow, and saying so would be a false apology attached to every plan
    /// naming <c>C:\Windows\MEMORY.DMP</c>.
    /// </summary>
    DirectRead,
}

/// <summary>
/// Why the fast path was unavailable. §5.5 requires the fallback to be *observable*: a scan that
/// silently takes the slow route looks identical to one that was simply given a big directory, and
/// the user is never told they could have elevated.
/// </summary>
public enum FallbackReason
{
    /// <summary>No fallback — the MFT was read.</summary>
    None,

    /// <summary>
    /// The process is not elevated. §6.3 says the app runs unelevated by default, so this is the
    /// ordinary case rather than an edge case, and the one the UI offers to fix.
    /// </summary>
    NotElevated,

    /// <summary>The volume is not NTFS — no MFT exists to read.</summary>
    NotNtfsVolume,

    /// <summary>The path is on a network share or a volume with no drive letter to open.</summary>
    VolumeNotAddressable,

    /// <summary>
    /// The volume handle opened and the table did not answer for this location: it could not be
    /// read or parsed, or it was read and does not establish a size for everything under the path.
    ///
    /// Distinct from the others because nothing the user can do changes it. A fragmented file whose
    /// sizes live outside its own record is ordinary rather than a fault, so this is not a report
    /// that anything is wrong — only that this location had to be walked.
    /// </summary>
    MasterFileTableIncomplete,

    /// <summary>
    /// The caller asked for a reading taken from the disk rather than from a snapshot, so the index
    /// was not consulted however complete it is.
    ///
    /// Not a fault, and nothing the user can act on: it is the executor measuring what a command
    /// freed, where a snapshot taken before the command would answer with the figure it is being
    /// subtracted from. See <see cref="IDirectoryScanner.MeasureFromDiskAsync"/>.
    /// </summary>
    FreshReadingRequired,
}

public static class FallbackReasonText
{
    /// <summary>The sentence shown beside a walked scan, or null when the table answered.</summary>
    public static string? Describe(FallbackReason reason) => reason switch
    {
        FallbackReason.None => null,
        FallbackReason.NotElevated =>
            "Scanned by walking directories. Running Deguffer as administrator lets it read the volume's "
            + "file table instead, which answers a location without walking it — though building the "
            + "table costs one pass over the whole volume first.",
        FallbackReason.NotNtfsVolume =>
            "Scanned by walking directories: this volume is not NTFS, so it has no file table to read.",
        FallbackReason.VolumeNotAddressable =>
            "Scanned by walking directories: this location is not on a local volume Deguffer can open.",
        FallbackReason.MasterFileTableIncomplete =>
            "Scanned by walking directories: the volume's file table did not account for everything here. "
            + "Sizes are still correct, but the scan took longer than it should.",

        // Nothing to say. Every other reason answers "why was this slower than it could have been",
        // which is a question about the preview the user is reading. This one belongs to the
        // executor's after-measure, where what reaches the user is the reclaim itself and how it was
        // arrived at is not a thing to explain.
        FallbackReason.FreshReadingRequired => null,

        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
