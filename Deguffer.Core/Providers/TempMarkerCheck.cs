using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every rule <see cref="TempMarkerSurvey"/> held a marker's entries back by, asked again in the
/// survey's order immediately before an entry it offered is removed.
///
/// <para>A marker can carry more than one: an updater's download waits for its application to close
/// and, as a folder, for no program to be working in it, and a Roslyn session answers to its mutex as
/// well as to the process table. The entry was offered because every rule said it was free, so any
/// one of them saying otherwise holds it back, with that rule's reason. The first to answer is the
/// one the survey would have reported, and the rest are not asked.</para>
/// </summary>
internal sealed class TempMarkerCheck(IReadOnlyList<IUseCheck> rules) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();

            if (rule.Ask(step, ct) is { Count: > 0 } inUse)
            {
                return inUse;
            }
        }

        return [];
    }
}
