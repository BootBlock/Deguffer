namespace Deguffer.Core.Exploring.History;

/// <summary>
/// Whether the Explore page draws the last kept scan of a drive while a new scan of it runs (#491),
/// and which scan that is for.
///
/// <para><b>Looked for as a scan starts</b> (<see cref="Begin"/>), unless the page already shows a
/// finished scan of that whole volume, which is newer than any kept one: the newest kept scan is taken
/// from such a tree, and a removal from the page changes the tree and not the summary. Whether the
/// scan covers a whole volume, and which, is not known yet, so it is offered later
/// (<see cref="Offered"/>), with nothing where the scan covers a folder or the volume has nothing
/// kept.</para>
///
/// <para><b>Shown only until the scan has a tree of its own.</b> The first snapshot or the finished
/// tree replaces it (<see cref="Replaced"/>), and so does the end of the scan however it ends, so a
/// cancelled scan leaves nothing of it behind. A kept scan offered after any of those, or for an
/// earlier scan, is not shown.</para>
///
/// <para><b>Nothing is acted on from it</b> (§7.1). It is shown only while a scan is busy, when
/// <see cref="ExploreGate.SelectionMayAct"/> is false, and nothing drawn of it answers the pointer
/// (<see cref="Rendering.MapReading.Answers"/>).</para>
/// </summary>
/// <param name="Scan">The scan this is for, numbered by the page, so an answer for an earlier one is told apart.</param>
/// <param name="Looking">Whether a kept scan offered for <paramref name="Scan"/> would be shown.</param>
/// <param name="Tree">The kept scan shown, or null.</param>
public readonly record struct KeptScanShown(int Scan, bool Looking, KeptScanTree? Tree)
{
    /// <summary>Whether the page draws a kept scan in place of the scan running.</summary>
    public bool Shows => Tree is not null;

    /// <summary>
    /// Scan number <paramref name="scan"/> starts on <paramref name="target"/>, with
    /// <paramref name="onScreen"/> on the page and <paramref name="onScreenVolume"/> beside it, which
    /// is <see cref="VolumeSpace.None"/> unless that tree is the whole of a volume.
    /// </summary>
    public static KeptScanShown Begin(int scan, string target, ExploreTree? onScreen, VolumeSpace onScreenVolume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var newer = onScreen is { IsSnapshot: false }
            && onScreenVolume != VolumeSpace.None
            && string.Equals(onScreen.RootPath, target, StringComparison.OrdinalIgnoreCase);

        return new KeptScanShown(scan, Looking: !newer, Tree: null);
    }

    /// <summary>
    /// <paramref name="kept"/> is what was found for scan number <paramref name="scan"/>: the last kept
    /// scan of the whole volume it covers, or null where it covers a folder or nothing is kept. Shown
    /// where this is still looking for that scan, and ignored otherwise.
    /// </summary>
    public KeptScanShown Offered(int scan, KeptScanTree? kept) =>
        scan == Scan && Looking ? this with { Looking = false, Tree = kept } : this;

    /// <summary>
    /// The scan has a tree of its own, or has ended: nothing is shown, and nothing offered later is.
    /// </summary>
    public KeptScanShown Replaced() => this with { Looking = false, Tree = null };
}
