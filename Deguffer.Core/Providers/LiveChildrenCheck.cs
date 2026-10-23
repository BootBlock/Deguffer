using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// <see cref="ILiveTreeInspector.FindLiveChildren"/> asked again immediately before the removal, for a
/// scratch folder whose entries a program may run from, work in, or have been started with.
///
/// <para><b>Two shapes, one question.</b> A folder cleared in place asks about its own entries, and
/// each one a program took up after the preview is spared as the plan spared the ones it found. An
/// entry removed on its own, such as a test browser's profile, asks about its folder's entries and
/// is held back where it is one of them.</para>
///
/// <para>An entry another row offers is not this step's to spare: the removal leaves it alone
/// already, and that row asks its own question.</para>
///
/// <para>A check that could not read every program's command line holds the whole step back, because
/// no entry can be spared on an answer that names none. See <see cref="LiveTreeVeto.AtClean"/>.</para>
/// </summary>
internal sealed class LiveChildrenCheck(ILiveTreeInspector inspector) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        var folder = step is ClearDirectoryStep ? step.Path : Path.GetDirectoryName(step.Path);

        if (folder is null)
        {
            return [];
        }

        // The inspector keeps one process table for a planning pass, and the preview's may be past
        // the limit.
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        var elsewhere = step is ClearDirectoryStep clear ? clear.OwnedElsewhere : [];
        var findings = inspector.FindLiveChildren([folder], ct);

        IReadOnlyList<InUseNow> inUse =
        [
            .. findings.Live
                .Where(live => LongPath.Contains(step.Path, live.Directory)
                    && !elsewhere.Any(entry => LongPath.Contains(entry, live.Directory)))
                .Select(live => new InUseNow(live.Directory, string.Join("; ", live.Holders))),
        ];

        return LiveTreeVeto.AtClean(step, inUse, findings.Complete);
    }
}
