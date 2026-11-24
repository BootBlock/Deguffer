using Deguffer.App.Shell;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The outcome of the latest change on a settings form, shown on the row that made it and on no other.
///
/// <para>A change that fails moves the notice to its own row, so the page never states one failure in
/// two places. A change that saves clears it, because the folder took a write, and a notice left on
/// another row would tell the reader to check a folder that is writable again. A change that asked
/// for what already holds wrote nothing, so it leaves the notice where it is: it is what the control
/// sends when it is put back after a refused write.</para>
/// </summary>
public sealed class LatestSave
{
    private SettingSave? _failed;

    /// <summary>Show whether the change made through <paramref name="setting"/> was written.</summary>
    /// <returns><paramref name="outcome"/>, so the caller can go on to read back what holds.</returns>
    public SaveOutcome Report(SettingSave setting, SaveOutcome outcome)
    {
        if (outcome is SaveOutcome.Unchanged)
        {
            return outcome;
        }

        var failed = outcome is SaveOutcome.Refused ? setting : null;

        // Left alone when the same row fails again, so its notice stays open rather than closing and
        // opening, which would play its arrival and scroll to it a second time.
        if (failed != _failed)
        {
            _failed?.Failed = false;
            _failed = failed;
            _failed?.Failed = true;
        }

        return outcome;
    }

    /// <summary>Show whether a list change made through <paramref name="setting"/> was written.</summary>
    public void Report(SettingSave setting, bool saved) =>
        Report(setting, saved ? SaveOutcome.Saved : SaveOutcome.Refused);
}
