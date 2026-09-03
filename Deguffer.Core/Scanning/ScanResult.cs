namespace Deguffer.Core.Scanning;

/// <summary>
/// One measurement, and how it was obtained. §5.5's fallback must be observable, so the route is
/// part of the result rather than something a caller has to infer from the elapsed time.
/// </summary>
/// <param name="WithheldRecent">
/// Whether a <see cref="Safety.MinimumAge"/> guard left at least one real file out of this figure.
///
/// <para>It exists because a zero is ambiguous once a guard is in force, and the shell makes a claim
/// out of a zero: a row with nothing to reclaim reads as "Already clear", which is a statement about
/// the folder. A cache whose every file is inside the window measures zero and is full, so the claim
/// would be false — and answering it from "the guard is on" instead would put the same wrong
/// sentence on every genuinely empty row, which is the mistake in the other direction. Only the
/// measurement knows which happened, so it is the measurement that says.</para>
///
/// <para>False wherever no guard was asked for, which keeps it exactly parallel to
/// <paramref name="Fallback"/>: a fact about how the number was arrived at, carried so that nothing
/// above has to infer it.</para>
/// </param>
public sealed record ScanResult(
    ScanSize Size,
    ScanStrategy Strategy,
    FallbackReason Fallback,
    bool WithheldRecent = false)
{
    public static ScanResult Fast(ScanSize size, bool withheldRecent = false) =>
        new(size, ScanStrategy.MasterFileTable, FallbackReason.None, withheldRecent);

    public static ScanResult Slow(ScanSize size, FallbackReason reason, bool withheldRecent = false) =>
        new(size, ScanStrategy.ParallelEnumeration, reason, withheldRecent);

    /// <summary>
    /// A walk that nothing fell back to: the caller had to enumerate, whatever the process token
    /// was, so there is no reason to carry and no elevation to offer.
    ///
    /// Distinct from <see cref="Slow"/> because the difference reaches the user.
    /// <see cref="FallbackReason"/> exists to say "this could have been quicker", and saying that
    /// beside a link-aware total would offer administrator rights that change the number not at all
    /// — the same false apology <see cref="ScanStrategy.DirectRead"/> exists to avoid.
    /// </summary>
    public static ScanResult ByChoice(ScanSize size, bool withheldRecent = false) =>
        new(size, ScanStrategy.ParallelEnumeration, FallbackReason.None, withheldRecent);

    /// <summary>
    /// One file, sized in a single read. No fallback reason, because nothing was fallen back from —
    /// see <see cref="ScanStrategy.DirectRead"/>.
    /// </summary>
    public static ScanResult Direct(ScanSize size, bool withheldRecent = false) =>
        new(size, ScanStrategy.DirectRead, FallbackReason.None, withheldRecent);

    /// <summary>The sentence to show beside the number, or null when the fast path was used.</summary>
    public string? FallbackNote => FallbackReasonText.Describe(Fallback);
}
