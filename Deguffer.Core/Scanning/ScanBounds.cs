namespace Deguffer.Core.Scanning;

/// <summary>
/// What a measurement leaves out of a tree, on the terms the removal of that tree leaves it out: the
/// root where the removal empties it in place, and the entries below it the removal neither takes nor
/// enters. <see cref="Execution.RemovalBounds"/> is the removal's half of the same answer.
///
/// <para><b>The figure is taken on the removal's terms rather than corrected afterwards.</b> An
/// entry left out is met once, as a name in its parent's listing, and never entered. Measuring it
/// again to subtract it would walk it a second time, one walk an entry, and a temporary folder can
/// hold thousands of entries another row owns, each of which that row walks as well.</para>
///
/// <para><c>default</c> is the whole tree, root included, which is what every caller that says
/// nothing has always measured.</para>
/// </summary>
/// <param name="KeepRoot">
/// Whether the directory measured stays standing, as it does for a folder emptied in place. Its own
/// entry is then never among those counted, which the measurement alone can know: a folder stays
/// while anything inside it stays, and the caller cannot see whether anything does.
/// </param>
/// <param name="LeftOut">
/// Paths below the measured one, in display form, that are neither counted nor entered, and that keep
/// every folder above them standing, as the removal leaves its spared and owned entries. A file may be
/// named as well as a folder. A path that is not below the measured one is ignored, and so is one
/// that names the measured path itself. Matched as the paths Windows opens them as, so a difference
/// in case, the <c>\\?\</c> prefix or an 8.3 alias on either side does not turn one entry into two.
/// </param>
public readonly record struct ScanBounds(bool KeepRoot, IReadOnlyList<string>? LeftOut = null)
{
    /// <summary>A folder emptied in place, with nothing below it held back.</summary>
    public static ScanBounds Contents => new(KeepRoot: true);

    /// <summary>
    /// Whether anything below the root is left out, which makes the figure one for this question alone
    /// rather than for the path.
    /// </summary>
    public bool LeavesEntriesOut => LeftOut is { Count: > 0 };
}
