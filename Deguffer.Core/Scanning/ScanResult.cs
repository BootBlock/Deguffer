namespace Deguffer.Core.Scanning;

/// <summary>
/// One measurement, and how it was obtained. §5.5's fallback must be observable, so the route is
/// part of the result rather than something a caller has to infer from the elapsed time.
/// </summary>
public sealed record ScanResult(ScanSize Size, ScanStrategy Strategy, FallbackReason Fallback)
{
    public static ScanResult Fast(ScanSize size) =>
        new(size, ScanStrategy.MasterFileTable, FallbackReason.None);

    public static ScanResult Slow(ScanSize size, FallbackReason reason) =>
        new(size, ScanStrategy.ParallelEnumeration, reason);

    /// <summary>The sentence to show beside the number, or null when the fast path was used.</summary>
    public string? FallbackNote => FallbackReasonText.Describe(Fallback);
}
