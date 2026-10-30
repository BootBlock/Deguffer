namespace Deguffer.Core.Execution.Sizes;

/// <summary>
/// What one Storage scan measured of one provider (#358): the figure its row stated, and when.
///
/// <para><b>A provider's id, a size and a time, and nothing that names a place.</b> The history is
/// kept so a row can say how far its cause grew, and that needs no path. Keeping it free of one means
/// the file can never describe the user's folders, whoever reads it.</para>
/// </summary>
/// <param name="TakenUtc">When the scan that measured it ran.</param>
/// <param name="Bytes">What the row stated it would reclaim, in bytes.</param>
/// <param name="Approximate">
/// Whether the figure was a prediction or a ceiling rather than a measurement
/// (<see cref="Scanning.ScanSize.IsApproximate"/>, <see cref="Scanning.ScanSize.IsCeiling"/>).
/// </param>
/// <param name="Elevated">
/// Whether the scan held administrator rights. One that did can read folders one without them cannot,
/// so a change between the two is partly the rights and not the cause.
/// </param>
public readonly record struct SizePoint(DateTime TakenUtc, long Bytes, bool Approximate, bool Elevated)
{
    /// <summary>
    /// What a scan measured of <paramref name="finding"/>'s provider, or null where it measured
    /// nothing: the tool is not here, nowhere is approved to look, or the location was not examined
    /// or could not be listed. Each of those states a zero that is not a size, and kept as one it would
    /// read as the cause being emptied.
    /// </summary>
    public static SizePoint? Of(Finding finding, DateTime takenUtc, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (!finding.IsPresent
            || finding.AwaitingSourceFolders
            || finding.Plan is not { HasUnreadableRoot: false, WasNotExamined: false })
        {
            return null;
        }

        var reclaim = finding.Reclaim;

        return new SizePoint(takenUtc, reclaim.Reclaimable, reclaim.IsApproximate || reclaim.IsCeiling, elevated);
    }
}
