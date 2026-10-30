using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution.Sizes;

/// <summary>
/// What one Storage scan measured of one provider (#358): the figure its row stated, and when.
///
/// <para><b>A provider's id, a size and a time, and nothing that names a place.</b> The history is
/// kept so a row can say how far its cause grew, and that needs no path. Keeping it free of one means
/// the file can never describe the user's folders, whoever reads it.</para>
/// </summary>
/// <param name="TakenUtc">When the scan that measured it ran.</param>
/// <param name="Bytes">What the row stated it would reclaim, in bytes (<see cref="ScanSize.Reclaimable"/>).</param>
/// <param name="Entries">How many entries that removal would take (<see cref="ScanSize.Entries"/>).</param>
/// <param name="Approximate">Whether the figure was a prediction rather than a measurement (<see cref="ScanSize.IsApproximate"/>).</param>
/// <param name="Ceiling">Whether the figure was the most a removal could free (<see cref="ScanSize.IsCeiling"/>).</param>
/// <param name="Elevated">
/// Whether the scan held administrator rights. One that did can read folders one without them cannot,
/// so a change between the two is partly the rights and not the cause.
/// </param>
public readonly record struct SizePoint(
    DateTime TakenUtc, long Bytes, long Entries, bool Approximate, bool Ceiling, bool Elevated)
{
    /// <summary>
    /// The figure as the row stated it, for <see cref="FreeSpace.Format(ScanSize)"/> to word, so the
    /// history says "about", "up to" or a count of items exactly where the row did.
    /// </summary>
    public ScanSize Size => new(Bytes, Bytes, Approximate, Entries, Ceiling);

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

        return new SizePoint(
            takenUtc, reclaim.Reclaimable, reclaim.Entries, reclaim.IsApproximate, reclaim.IsCeiling, elevated);
    }
}
