namespace Deguffer.Core.Execution;

/// <summary>
/// What is running in <see cref="RunningActions"/>, in the words a sentence about it uses.
///
/// <para>Shared by every sentence that has to say what is running, so the close prompt and the
/// countdown after a clean cannot come to name the same run two ways.</para>
/// </summary>
public static class RunningActionText
{
    /// <summary>Every action, as a list read aloud: "a, b and c".</summary>
    public static string List(IReadOnlyList<RunningAction> running) =>
        running.Count == 1
            ? Name(running[0])
            : string.Join(", ", running.Take(running.Count - 1).Select(Name)) + " and " + Name(running[^1]);

    /// <summary>"it" for one action and "they" for more, with the verb that agrees with it.</summary>
    public static (string It, string Finishes) Agreement(IReadOnlyList<RunningAction> running) =>
        running.Count == 1 ? ("it", "finishes") : ("they", "finish");

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
