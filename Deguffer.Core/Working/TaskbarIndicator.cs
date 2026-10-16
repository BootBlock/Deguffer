namespace Deguffer.Core.Working;

/// <summary>
/// What the taskbar button says about <see cref="BusyWork"/>: a bar while anything is under way, and
/// a call for attention when the work ends while the user is in another program.
///
/// <para>The bar is indeterminate while any span cannot say how far it has got, because a fill made
/// from the others would claim a measure the whole does not have. Otherwise it is filled to the mean
/// of the spans' fractions, so two runs at once read as one run half done when each is half done.</para>
///
/// <para>A failure the user could not see, because they were in another program, leaves the bar red
/// once everything has ended, and the red stays until the main window is next activated
/// (<see cref="Seen"/>). A failure in front of the user is shown by the page, and a red bar would
/// then stay up with nothing to clear it, because a window already in front is not activated
/// again.</para>
///
/// <para>Whether the user is in this program is asked at the moment it matters rather than followed
/// from the main window's activation. One of the app's own tool windows takes the activation from
/// the main window without the user leaving the app, and leaving that tool window for another
/// program raises nothing on the main window at all.</para>
///
/// <para>Decides only. The window writes <see cref="Current"/> to its taskbar button, and flashes the
/// button on <see cref="AttentionWanted"/>. Used from the window's thread only, as
/// <see cref="BusyWork"/> is.</para>
/// </summary>
public sealed class TaskbarIndicator
{
    private readonly BusyWork _work;
    private readonly Func<bool> _userIsHere;
    private bool _failedInBackground;
    private bool _wasIdle;

    /// <param name="userIsHere">Whether the window the user is working in is one of this program's.</param>
    public TaskbarIndicator(BusyWork work, Func<bool> userIsHere)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(userIsHere);

        _work = work;
        _userIsHere = userIsHere;
        _wasIdle = work.IsIdle;
        _work.Changed += (_, _) => Follow();
        _work.Failed += (_, _) => _failedInBackground |= !_userIsHere();
        Current = Measure();
    }

    /// <summary>What the bar shows now.</summary>
    public TaskbarProgress Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the last work ends while the user is in another program.</summary>
    public event EventHandler? AttentionWanted;

    /// <summary>
    /// The main window was activated, so the user is looking at the page that says what failed.
    /// </summary>
    public void Seen()
    {
        if (_failedInBackground)
        {
            _failedInBackground = false;
            Show();
        }
    }

    private void Follow()
    {
        var idle = _work.IsIdle;
        var ended = idle && !_wasIdle;
        _wasIdle = idle;

        Show();

        if (ended && !_userIsHere())
        {
            AttentionWanted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Show()
    {
        var now = Measure();

        if (now == Current)
        {
            return;
        }

        Current = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TaskbarProgress Measure()
    {
        var spans = _work.Current;

        if (spans.Count == 0)
        {
            return _failedInBackground ? TaskbarProgress.Error : TaskbarProgress.None;
        }

        double sum = 0;

        foreach (var span in spans)
        {
            if (span.Fraction is not { } fraction)
            {
                return TaskbarProgress.Indeterminate;
            }

            sum += fraction;
        }

        return TaskbarProgress.Filled(sum / spans.Count);
    }
}
