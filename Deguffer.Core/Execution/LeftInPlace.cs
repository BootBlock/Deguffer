using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The words a removal step uses for what it left where it was.
///
/// <para>The causes are named separately because they ask different things of the reader. Another
/// program has files open, or is working in a folder, which closing it answers. Windows would not let
/// Deguffer remove files or folders, which waiting will not change. Windows would not let Deguffer look
/// inside a folder, so what it holds was never tried. A setting they chose held files
/// back. Deguffer declined to touch an entry it found somebody working in. And an Outlook mail store, or
/// work an editor keeps in a temporary folder until it is saved, is never removed at all, which asks
/// nothing of them. A single count would tell them how much stayed and nothing about what to do.</para>
///
/// <para>Composed from the clauses that apply rather than switched on every combination: nine causes
/// make five hundred and twelve cases, and the wording would then live in every one of them.</para>
/// </summary>
internal static class LeftInPlace
{
    /// <summary>
    /// The sentence for a folder step that found a file at its path. See
    /// <see cref="RemovalOutcome.NotADirectory"/>.
    /// </summary>
    public const string NotAFolder = "Left alone: a file now stands where this folder was, so nothing was removed.";

    /// <summary>
    /// What a step that achieved something has to add about what it left behind, or nothing where it
    /// left nothing.
    /// </summary>
    /// <param name="unlisted">How many folders Windows would not let the removal list. See <see cref="RemovalOutcome.Unlisted"/>.</param>
    public static string Clauses(
        Refusals refused,
        FolderRefusals folders,
        int kept,
        int spared = 0,
        int mailStores = 0,
        int userWork = 0,
        int unlisted = 0)
    {
        var clauses = Each(refused, folders, kept, spared, mailStores, userWork, unlisted);

        return clauses.Count == 0 ? string.Empty : ", " + string.Join(", ", clauses);
    }

    /// <summary>
    /// Each cause of what was left behind as a clause of its own, in the order <see cref="Clauses"/>
    /// joins them, or none where nothing was left.
    ///
    /// <para>Apart from the joined form because a finished clean lists them a line each under the row
    /// they belong to (<see cref="RunRow"/>), and two wordings of the same cause would drift.</para>
    /// </summary>
    /// <param name="unreleased">What Windows kept that was measured but not counted. See <see cref="StepOutcome.Unreleased"/>.</param>
    public static IReadOnlyList<string> Each(
        Refusals refused,
        FolderRefusals folders,
        int kept,
        int spared = 0,
        int mailStores = 0,
        int userWork = 0,
        int unlisted = 0,
        long unreleased = 0)
    {
        var clauses = new List<string>(10);

        if (refused.InUse.Files > 0)
        {
            clauses.Add($"{Files(refused.InUse)} left in place because another program had {Them(refused.InUse.Files)} open");
        }

        if (refused.Denied.Files > 0)
        {
            clauses.Add($"{Files(refused.Denied)} left in place because Windows would not let Deguffer remove {Them(refused.Denied.Files)}");
        }

        if (unreleased > 0)
        {
            clauses.Add($"{FreeSpace.Format(unreleased)} left in place because Windows would not release it");
        }

        if (folders.InUse > 0)
        {
            clauses.Add($"{Folders(folders.InUse)} left in place because another program was using {Them(folders.InUse)}");
        }

        if (folders.Denied > 0)
        {
            clauses.Add($"{Folders(folders.Denied)} left in place because Windows would not let Deguffer remove {Them(folders.Denied)}");
        }

        if (unlisted > 0)
        {
            clauses.Add($"{Folders(unlisted)} left in place because Windows would not let Deguffer look inside {Them(unlisted)}");
        }

        if (kept > 0)
        {
            clauses.Add($"{Counted.Of(kept, "file", "files")} left alone because {Counted.Form(kept, "it", "they")} changed recently");
        }

        if (spared > 0)
        {
            clauses.Add($"{Counted.Of(spared, "item", "items")} left alone because something is using {Them(spared)}");
        }

        if (userWork > 0)
        {
            clauses.Add(
                $"{Counted.Of(userWork, "item", "items")} left alone because "
                + $"{Counted.Form(userWork, "it holds", "they hold")} work that was never saved anywhere else");
        }

        if (mailStores > 0)
        {
            clauses.Add($"{Counted.Of(mailStores, "Outlook data file", "Outlook data files")} left alone, because Deguffer never removes one");
        }

        return clauses;
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
    /// <param name="unlisted">How many folders Windows would not let the removal list. See <see cref="RemovalOutcome.Unlisted"/>.</param>
    public static string WhyNothingHappened(Refusals refused, FolderRefusals folders, int unlisted = 0)
    {
        if (refused.IsEmpty && folders.IsEmpty && unlisted == 0)
        {
            return "Nothing was removed.";
        }

        var causes = new List<string>(5);

        if (refused.Denied.Files > 0)
        {
            causes.Add($"Windows would not let Deguffer remove {Files(refused.Denied)}");
        }

        if (refused.InUse.Files > 0)
        {
            causes.Add($"another program had {Files(refused.InUse)} open");
        }

        if (folders.Denied > 0)
        {
            causes.Add($"Windows would not let Deguffer remove {Folders(folders.Denied)}");
        }

        if (folders.InUse > 0)
        {
            causes.Add($"another program was using {Folders(folders.InUse)}");
        }

        if (unlisted > 0)
        {
            causes.Add($"Windows would not let Deguffer look inside {Folders(unlisted)}");
        }

        return $"Nothing was removed: {string.Join(", and ", causes)}.";
    }

    private static string Files(RefusalTally tally) =>
        $"{Counted.Of(tally.Files, "file", "files")} ({FreeSpace.Format(tally.Bytes)})";

    private static string Folders(int count) => Counted.Of(count, "folder", "folders");

    private static string Them(int count) => Counted.Form(count, "it", "them");
}
