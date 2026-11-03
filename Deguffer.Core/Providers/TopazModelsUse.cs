using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether something may be loading an installed Topaz product's models now, asked while planning and
/// again immediately before each removal (§5.3).
///
/// <para><b>Two signals.</b> A program running from a Topaz program folder is a Topaz program,
/// whatever it is called: Topaz Video renders through the <c>ffmpeg.exe</c> it installs beside itself,
/// which reads the models with no window open, and a process name could not tell that copy from any
/// other. And by name, the applications themselves and the image editors Topaz's plugins were reported
/// running inside, which run from somewhere else. Either one holds back every installed product's
/// models, because nothing says which product a program is loading for. A question that could not be
/// asked in full is not a clear answer.</para>
/// </summary>
/// <param name="Processes">What reads the process table for the names.</param>
/// <param name="LiveTrees">What reads where each program runs and works.</param>
/// <param name="ProgramFolders">The Topaz program folders, in display form.</param>
internal sealed record TopazModelsUse(
    IProcessInspector Processes,
    ILiveTreeInspector LiveTrees,
    IReadOnlyList<string> ProgramFolders) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // Both inspectors keep one reading for a planning pass, and the preview's may be past the limit.
        Processes.DiscardOlderThan(IUseCheck.ReadingLimit);
        LiveTrees.DiscardOlderThan(IUseCheck.ReadingLimit);

        return WhyInUse(ct) is { } reason ? [new InUseNow(step.Path, reason)] : [];
    }

    /// <summary>
    /// What may be loading the models now, as a lower-case clause with no full stop, or null where
    /// nothing is.
    /// </summary>
    public string? WhyInUse(CancellationToken ct)
    {
        if (Processes.FindRunning(TopazLayout.ProcessNames) is { Count: > 0 } running)
        {
            return $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running now";
        }

        if (ProgramFolders.Count == 0)
        {
            return null;
        }

        var findings = LiveTrees.FindLive([.. ProgramFolders.Select(folder => new LiveTreeQuery(folder, folder))], ct);

        if (findings.Live.Count > 0)
        {
            return $"{string.Join("; ", findings.Live.SelectMany(live => live.Holders).Distinct(StringComparer.OrdinalIgnoreCase))} "
                + "is running from a Topaz program folder now";
        }

        return ProgramFolders.All(findings.IsAnswered) ? null : $"{LiveTreeVeto.CannotTell} folder";
    }
}
