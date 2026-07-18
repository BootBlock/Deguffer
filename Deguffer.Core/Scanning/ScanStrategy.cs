namespace Deguffer.Core.Scanning;

/// <summary>Which of §5.5's two measurement routes produced a number.</summary>
public enum ScanStrategy
{
    /// <summary>The MFT was read directly — one pass per volume, then every query is a lookup.</summary>
    MasterFileTable,

    /// <summary>
    /// §5.5's fallback: bounded parallel directory enumeration. Correct, and too slow to be the
    /// scanner on its own, which is why reaching it is always accompanied by a reason.
    /// </summary>
    ParallelEnumeration,
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
    /// The volume handle opened but the MFT could not be read or parsed. Distinct from the others
    /// because it is not expected: it means either an unfamiliar on-disk layout or a genuine bug,
    /// and it should be visible rather than absorbed.
    /// </summary>
    MasterFileTableUnreadable,
}

public static class FallbackReasonText
{
    /// <summary>The sentence shown beside a slow scan, or null when the fast path was used.</summary>
    public static string? Describe(FallbackReason reason) => reason switch
    {
        FallbackReason.None => null,
        FallbackReason.NotElevated =>
            "Scanned by walking directories, which is slower. Run Deguffer as administrator to read the "
            + "file table directly.",
        FallbackReason.NotNtfsVolume =>
            "Scanned by walking directories: this volume is not NTFS, so it has no file table to read.",
        FallbackReason.VolumeNotAddressable =>
            "Scanned by walking directories: this location is not on a local volume Deguffer can open.",
        FallbackReason.MasterFileTableUnreadable =>
            "Scanned by walking directories: the volume's file table could not be read. Sizes are still "
            + "correct, but the scan took longer than it should.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
