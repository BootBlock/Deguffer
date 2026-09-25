using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// A marker's own word on whether an entry is live, asked again immediately before the entry the plan
/// offered because the tool said it was not.
///
/// <para><b>The marker's question, not a copy of it.</b> The check calls
/// <see cref="TempMarker.InUse"/> itself, so it cannot drift from the rule the plan applied. For a
/// Roslyn session that is whether the mutex named after the folder exists. A mutex that exists at the
/// clean is a session holding the folder, whatever the preview saw, and a lookup Windows will not
/// answer reads as one, as it did at the preview.</para>
/// </summary>
internal sealed class TempMarkerEntryCheck(Func<string, bool> inUse, string reason) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct) =>
        inUse(Path.GetFileName(Path.TrimEndingDirectorySeparator(step.Path)))
            ? [new InUseNow(step.Path, reason)]
            : [];
}
