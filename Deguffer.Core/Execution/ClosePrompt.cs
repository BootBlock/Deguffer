namespace Deguffer.Core.Execution;

/// <summary>
/// What the user is asked when they close the window while something in
/// <see cref="RunningActions"/> runs.
///
/// <para>A Core type for the reason the other prompts are: the sentence that stands between the
/// user and an interrupted run is held to a test rather than living inside a dialog. It names what
/// is running, says what closing now would lose, and offers the close the user wanted without that
/// loss.</para>
/// </summary>
/// <param name="Title">The question, naming what is running.</param>
/// <param name="Consequence">What closing now would lose, and what the affirmative button does instead.</param>
/// <param name="CloseWhenDoneLabel">The affirmative button, which closes Deguffer once the run is over.</param>
/// <param name="KeepOpenLabel">The button that leaves the window open.</param>
public sealed record ClosePrompt(string Title, string Consequence, string CloseWhenDoneLabel, string KeepOpenLabel)
{
    /// <param name="running">What is running, from <see cref="RunningActions.Current"/>.</param>
    public static ClosePrompt For(IReadOnlyList<RunningAction> running)
    {
        // A close with nothing running goes ahead without asking, so a prompt about nothing is a
        // dialog that should never have been built.
        ArgumentOutOfRangeException.ThrowIfZero(running.Count);

        var what = running.Count == 1
            ? Name(running[0])
            : string.Join(", ", running.Take(running.Count - 1).Select(Name)) + " and " + Name(running[^1]);

        var (it, finishes) = running.Count == 1 ? ("it", "finishes") : ("they", "finish");

        return new ClosePrompt(
            $"Close Deguffer while {what} {(running.Count == 1 ? "is" : "are")} running?",

            $"Closing Deguffer now stops {what} part-way. Deguffer then cannot check what was changed "
            + $"or tell you what was done. Deguffer can close by itself as soon as {it} {finishes}.",

            $"Close when {it} {finishes}",
            "Keep Deguffer open");
    }

    private static string Name(RunningAction action) => action switch
    {
        RunningAction.StorageClean => "a clean on the Storage page",
        RunningAction.ExploreRemoval => "a removal on the Explore page",
        RunningAction.EntryRemoval => "a removal of installed app entries",
        RunningAction.BackupRestore => "a restore of a registry backup",
        RunningAction.Uninstall => "an uninstall",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
