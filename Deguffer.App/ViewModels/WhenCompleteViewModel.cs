using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The "When complete" box beside Clean: what it lists, what is chosen, and carrying the choice out
/// once a clean has finished.
///
/// <para>Whether a choice is offered and whether a finished clean is followed by it are
/// <see cref="WhenCleanComplete"/>'s to decide, and when it is due is
/// <see cref="CompletionCountdown"/>'s. This type wires them to the page.</para>
/// </summary>
public sealed partial class WhenCompleteViewModel : ObservableObject
{
    private readonly PreferenceService _preferences;
    private readonly IWindowsSession _session;
    private readonly IReadOnlyList<CompletionAction> _offered;

    private int _selectedIndex;

    public WhenCompleteViewModel(PreferenceService preferences, IWindowsSession session)
    {
        _preferences = preferences;
        _session = session;
        _offered = WhenCleanComplete.Offered(session.CanSleep, session.CanHibernate);
        Labels = [.. _offered.Select(WhenCleanComplete.Label)];

        // Not written back. A stored choice this machine does not offer is shown as Do nothing and
        // left on disk, so switching hibernation back on brings it back.
        _selectedIndex = WhenCleanComplete.IndexOf(preferences.Current.WhenCleanComplete, _offered);
    }

    public IReadOnlyList<string> Labels { get; }

    public string Explanation => WhenCleanComplete.Explanation;

    /// <summary>
    /// The chosen entry in <see cref="Labels"/>, bound both ways to the box.
    ///
    /// <para>Applied first and persisted second, as the page's View box is. What the box shows is
    /// what will happen after this clean, whether or not the preferences file can be written, and a
    /// failed write costs only the choice at the next launch.</para>
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            // The box reports -1 while its items are being replaced. That is not a choice.
            if (value < 0 || value >= _offered.Count || value == _selectedIndex)
            {
                return;
            }

            _selectedIndex = value;
            OnPropertyChanged();

            var chosen = Selected;
            _preferences.Update(current => current with { WhenCleanComplete = chosen });
        }
    }

    public CompletionAction Selected => _offered[_selectedIndex];

    /// <summary>
    /// Counts down to the action and returns whether it is due: true once the count runs out or the
    /// user acts now, false where they cancel or nothing could be asked. The view supplies it, and
    /// leaving it null means nothing is ever carried out.
    /// </summary>
    public Func<CompletionCountdown, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Raised when the user's choice was to close Deguffer, which only the window can do.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Carry out the chosen action after a clean that ended with <paramref name="outcome"/>, where
    /// the rules allow it and the countdown runs out. Returns a sentence for the user where Windows
    /// refused it, or null.
    ///
    /// <para>The choice is read here, when the clean ends, and not when it starts. Choosing to shut
    /// down part-way through a long clean is the ordinary way to use this.</para>
    /// </summary>
    public async Task<string?> FollowAsync(RunOutcome outcome)
    {
        var action = Selected;

        if (!WhenCleanComplete.Follows(action, outcome) || ConfirmAsync is not { } confirm)
        {
            return null;
        }

        if (!await confirm(new CompletionCountdown(action)))
        {
            return null;
        }

        if (action == CompletionAction.ExitDeguffer)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return null;
        }

        // Off the window's thread: SetSuspendState does not return until the machine wakes.
        var refusal = await Task.Run(() => _session.Perform(action));

        return refusal is null ? null : WhenCleanComplete.Refused(action, refusal);
    }
}
