using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The words a removal step uses for what it left where it was.
///
/// <para>The causes are named separately because they ask different things of the reader. Another
/// program has files open, which closing it answers. Windows would not let Deguffer remove files,
/// which waiting will not change. A setting they chose held files back. And Deguffer declined to
/// touch an entry it found somebody working in. A single count would tell them how much stayed and
/// nothing about what to do.</para>
///
/// <para>Composed from the clauses that apply rather than switched on every combination: four causes
/// make sixteen cases, and the wording would then live in sixteen places.</para>
/// </summary>
internal static class LeftInPlace
{
    /// <summary>
    /// What a step that achieved something has to add about what it left behind, or nothing where it
    /// left nothing.
    /// </summary>
    public static string Clauses(Refusals refused, int kept, int spared = 0)
    {
        var clauses = new List<string>(4);

        if (refused.InUse.Files > 0)
        {
            clauses.Add($"{Files(refused.InUse)} left in place because another program had them open");
        }

        if (refused.Denied.Files > 0)
        {
            clauses.Add($"{Files(refused.Denied)} left in place because Windows would not let Deguffer remove them");
        }

        if (kept > 0)
        {
            clauses.Add($"{kept} file(s) left alone because they changed recently");
        }

        if (spared > 0)
        {
            clauses.Add($"{spared} item(s) left alone because something is using them");
        }

        return clauses.Count == 0 ? string.Empty : ", " + string.Join(", ", clauses);
    }

    /// <summary>
    /// The sentence for a deletion that achieved nothing.
    ///
    /// <para>It names the kind of refusal the deletion reported, and nothing beyond it. Which of the
    /// two happened is read from the exception, so it is known; <em>why</em> Windows denied a file is
    /// not. An unelevated delete under the Windows directory, security software guarding a folder,
    /// and an executable still running all arrive as the same denial. Naming one of them would be a
    /// guess, and "run as administrator" is advice this is almost only shown to somebody who has
    /// taken — the shell does not offer a step needing those rights to a process without them.</para>
    /// </summary>
    public static string WhyNothingHappened(Refusals refused)
    {
        if (refused.IsEmpty)
        {
            return "Nothing was removed.";
        }

        var causes = new List<string>(2);

        if (refused.Denied.Files > 0)
        {
            causes.Add($"Windows would not let Deguffer remove {Files(refused.Denied)}");
        }

        if (refused.InUse.Files > 0)
        {
            causes.Add($"another program had {Files(refused.InUse)} open");
        }

        return $"Nothing was removed: {string.Join(", and ", causes)}.";
    }

    private static string Files(RefusalTally tally) =>
        $"{tally.Files:N0} file(s) ({FreeSpace.Format(tally.Bytes)})";
}
