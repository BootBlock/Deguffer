namespace Deguffer.Core.Execution;

/// <summary>
/// Whether the window may close now, and closing it once what is running has finished where the
/// user asked for that.
///
/// <para>A close is held while anything in <see cref="RunningActions"/> runs, because ending the
/// process there loses the run's §5.6 verification and its report. The user is asked, and may
/// choose to have Deguffer close itself once the run is over. That choice stands until they make
/// the other one: a second press of the close button asks again, and keeping the window open
/// withdraws it.</para>
/// </summary>
public sealed class CloseGuard
{
    private readonly RunningActions _running;

    public CloseGuard(RunningActions running)
    {
        _running = running;
        _running.Changed += (_, _) => CloseIfWaitingAndIdle();
    }

    /// <summary>
    /// Raised when the window should close: once nothing is running, after the user asked for that.
    /// Raised at most once for each time they asked.
    /// </summary>
    public event EventHandler? ReadyToClose;

    /// <summary>Whether the window closes by itself once nothing is running.</summary>
    public bool ClosesWhenIdle { get; private set; }

    /// <summary>Whether a close may go ahead without asking, because nothing is running.</summary>
    public bool MayClose => !_running.Any;

    /// <summary>
    /// The user chose to close once what is running has finished. Where it finished while they were
    /// being asked, <see cref="ReadyToClose"/> is raised before this returns.
    /// </summary>
    public void CloseWhenIdle()
    {
        ClosesWhenIdle = true;
        CloseIfWaitingAndIdle();
    }

    /// <summary>The user chose to keep the window open, which withdraws an earlier choice to close.</summary>
    public void KeepOpen() => ClosesWhenIdle = false;

    private void CloseIfWaitingAndIdle()
    {
        if (!ClosesWhenIdle || _running.Any)
        {
            return;
        }

        ClosesWhenIdle = false;
        ReadyToClose?.Invoke(this, EventArgs.Empty);
    }
}
