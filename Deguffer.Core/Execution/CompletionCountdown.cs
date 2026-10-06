namespace Deguffer.Core.Execution;

/// <summary>
/// The warning that stands between a finished clean and the <see cref="CompletionAction"/> that
/// follows it: how long is left, what the user is told, and when the action is due.
///
/// <para><b>Why there is a countdown at all.</b> The choice is stored, so the clean that carries it
/// out can be days after it was made, and a restart or a log-off closes every program on the
/// machine with whatever is unsaved in them. Thirty seconds, which is what Windows' own
/// <c>shutdown</c> command waits by default, is long enough for somebody at the machine to stop it
/// and short enough that nobody who walked away is kept waiting.</para>
///
/// <para><b>Why it waits for the rest of Deguffer.</b> Every page keeps running while another is on
/// screen, so a removal on the Explore page can still be running when a clean ends. Ending the
/// process under it loses its §5.6 verification and its report, which is what
/// <see cref="CloseGuard"/> holds a close for. The countdown starts again from the top for as long
/// as anything in <see cref="RunningActions"/> runs, so the user always gets the whole warning and
/// the action is never due while something is still changing the machine.</para>
/// </summary>
public sealed class CompletionCountdown
{
    /// <summary>How many seconds the user is given to stop the action.</summary>
    public const int Seconds = 30;

    public CompletionCountdown(CompletionAction action)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(action, CompletionAction.Nothing);

        Action = action;
    }

    public CompletionAction Action { get; }

    /// <summary>How many seconds are left before the action is due.</summary>
    public int SecondsLeft { get; private set; } = Seconds;

    public string Title => "Clean finished";

    /// <summary>The button that carries the action out without waiting.</summary>
    public string ActNowLabel => Action switch
    {
        CompletionAction.ExitDeguffer => "Exit now",
        CompletionAction.Lock => "Lock now",
        CompletionAction.LogOff => "Log off now",
        CompletionAction.Sleep => "Sleep now",
        CompletionAction.Hibernate => "Hibernate now",
        CompletionAction.Restart => "Restart now",
        CompletionAction.ShutDown => "Shut down now",
        _ => throw new InvalidOperationException($"No countdown is built for {Action}."),
    };

    /// <summary>The button that stops the action. Leaving the machine as it is, is the safe default.</summary>
    public string CancelLabel => "Cancel";

    /// <summary>Whether the action may be carried out now, because nothing else in Deguffer is running.</summary>
    public static bool MayActNow(IReadOnlyList<RunningAction> running) => running.Count == 0;

    /// <summary>
    /// One second has passed while <paramref name="running"/> runs. Returns whether the action is now
    /// due. While anything runs, the count goes back to the top rather than on down.
    /// </summary>
    public bool Tick(IReadOnlyList<RunningAction> running)
    {
        if (!MayActNow(running))
        {
            SecondsLeft = Seconds;
            return false;
        }

        SecondsLeft = Math.Max(0, SecondsLeft - 1);

        return SecondsLeft == 0;
    }

    /// <summary>What the dialog says, for the state of <paramref name="running"/> now.</summary>
    public string Sentence(IReadOnlyList<RunningAction> running)
    {
        var doing = WhenCleanComplete.Doing(Action);

        if (!MayActNow(running))
        {
            var (_, finishes) = RunningActionText.Agreement(running);

            return $"Deguffer will {doing} {Seconds} seconds after {RunningActionText.List(running)} {finishes}.";
        }

        var when = SecondsLeft == 1 ? "1 second" : $"{SecondsLeft} seconds";

        return $"Deguffer will {doing} in {when}." + Action switch
        {
            // Each of these closes every program on the machine. Windows asks them to close first,
            // and one with unsaved work may hold the session open, but none of that is a promise
            // to make on Windows' behalf.
            CompletionAction.LogOff or CompletionAction.Restart or CompletionAction.ShutDown =>
                " Save your work in other programs first.",
            _ => string.Empty,
        };
    }
}
