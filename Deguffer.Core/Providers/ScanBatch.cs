using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sizes of one plan's paths, and how they were obtained.
///
/// The two travel together deliberately. §5.5 requires the fallback to be observable, and a
/// provider that measured its paths but forgot to ask separately for the reason would silently drop
/// the elevation prompt with nothing to catch it.
/// </summary>
/// <param name="Sizes">One entry per path measured, in the order they were given.</param>
/// <param name="WithheldRecent">
/// One entry per path, saying whether the user's guard left a real file out of that path's
/// figure. Per path rather than one flag for the batch, because it becomes a property of the step
/// that deletes that path.
/// </param>
/// <param name="Fallback">
/// Which route served the measurement. Carried as the enum rather than only its sentence because
/// the UI has to decide whether elevating would actually help, and matching on display text to
/// answer that is how a reworded string silently disables the offer.
/// </param>
/// <param name="MailStores">
/// One entry per path: the Outlook mail stores its measurement left out, by path. Per path for the
/// reason <paramref name="WithheldRecent"/> is, because each becomes a property of the step that
/// removes that path. See <see cref="CleanupStep.MailStores"/>.
/// </param>
public sealed record ScanBatch(
    IReadOnlyList<ScanSize> Sizes,
    FallbackReason Fallback,
    IReadOnlyList<bool> WithheldRecent,
    IReadOnlyList<IReadOnlyList<string>> MailStores)
{
    public ScanSize Total => Sizes.Aggregate(ScanSize.Zero, (sum, size) => sum + size);

    /// <summary>
    /// Every mail store across every path measured, for a step that reports against all of them at
    /// once — a tool's own command, whose probe is the whole batch.
    /// </summary>
    public IReadOnlyList<string> AllMailStores => [.. MailStores.SelectMany(stores => stores)];

    /// <summary>The scan-route note, or null when the fast path served every path.</summary>
    public PlanNote? Note => FallbackReasonText.Describe(Fallback) is { } text
        ? new PlanNote(PlanNoteSeverity.Information, text)
        : null;
}
