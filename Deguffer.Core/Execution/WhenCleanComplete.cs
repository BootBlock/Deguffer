namespace Deguffer.Core.Execution;

/// <summary>
/// Which <see cref="CompletionAction"/>s the Storage page offers, what each is called, and whether a
/// finished clean is followed by the one the user chose.
///
/// <para>Here rather than in the view-model because each answer decides whether Deguffer ends the
/// user's session or turns their machine off, and a rule that does that is held to a test.</para>
/// </summary>
public static class WhenCleanComplete
{
    /// <summary>
    /// The choices this machine can carry out, in <see cref="CompletionAction"/>'s order.
    ///
    /// <para>Sleep and hibernation are left out where Windows says the machine cannot do them, rather
    /// than offered and refused after a long clean. A machine with Modern Standby reports no sleep
    /// state a program may ask for, and hibernation is switched off on many machines.</para>
    /// </summary>
    public static IReadOnlyList<CompletionAction> Offered(bool canSleep, bool canHibernate) =>
        [.. Enum.GetValues<CompletionAction>().Where(action => action switch
        {
            CompletionAction.Sleep => canSleep,
            CompletionAction.Hibernate => canHibernate,
            _ => true,
        })];

    /// <summary>
    /// Where <paramref name="stored"/> sits in <paramref name="offered"/>, or the place of
    /// <see cref="CompletionAction.Nothing"/> where it is not there.
    ///
    /// <para>A stored choice can be missing from the list: hibernation switched off since it was
    /// chosen, or a number edited into the settings file by hand. Nothing is the only safe reading of
    /// either, because any other would end a session the user did not ask to end.</para>
    /// </summary>
    public static int IndexOf(CompletionAction stored, IReadOnlyList<CompletionAction> offered)
    {
        for (var i = 0; i < offered.Count; i++)
        {
            if (offered[i] == stored)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a clean that ended with <paramref name="outcome"/> is followed by
    /// <paramref name="action"/>.
    ///
    /// <para>Not after a cancelled clean, because whoever cancelled it is at the machine. That covers
    /// a Cancel pressed once the deletions were over, while the changed rows were planned again: the
    /// run itself was not interrupted, so <paramref name="outcome"/> does not say so, and
    /// <paramref name="cancelPressed"/> does. And not
    /// after a §5.6 verification failure: that is the one sentence the user must read before the next
    /// run, and a closed window or a machine that is off would take it with it. A clean that never
    /// ran, because nothing was confirmed or it failed, has no outcome and never reaches here.</para>
    /// </summary>
    public static bool Follows(CompletionAction action, RunOutcome outcome, bool cancelPressed) =>
        action != CompletionAction.Nothing && !outcome.Cancelled && !cancelPressed && !outcome.VerificationFailed;

    /// <summary>What the box on the Storage page is for, and what keeps it from surprising anybody.</summary>
    public static string Explanation =>
        $"What Deguffer does once a clean has finished. It counts down for {CompletionCountdown.Seconds} "
        + "seconds first, so you can cancel it. Nothing happens after a clean that was cancelled, or "
        + "whose check of protected paths failed.";

    /// <summary>The choice's name in the list on the Storage page.</summary>
    public static string Label(CompletionAction action) => action switch
    {
        CompletionAction.Nothing => "Do nothing",
        CompletionAction.ExitDeguffer => "Exit Deguffer",
        CompletionAction.Lock => "Lock PC",
        CompletionAction.LogOff => "Log off",
        CompletionAction.Sleep => "Sleep",
        CompletionAction.Hibernate => "Hibernate",
        CompletionAction.Restart => "Restart PC",
        CompletionAction.ShutDown => "Shut down PC",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>What the choice does, as a phrase that follows "Deguffer will".</summary>
    public static string Doing(CompletionAction action) => action switch
    {
        CompletionAction.ExitDeguffer => "close",
        CompletionAction.Lock => "lock this PC",
        CompletionAction.LogOff => "log you off",
        CompletionAction.Sleep => "put this PC to sleep",
        CompletionAction.Hibernate => "hibernate this PC",
        CompletionAction.Restart => "restart this PC",
        CompletionAction.ShutDown => "shut down this PC",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>
    /// What the user is told where Windows refused <paramref name="action"/>, with Windows' own
    /// reason.
    /// </summary>
    public static string Refused(CompletionAction action, string reason) =>
        $"The clean finished, but Deguffer could not {Doing(action)}. {reason}";
}
