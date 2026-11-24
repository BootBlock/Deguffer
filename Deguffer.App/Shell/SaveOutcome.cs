namespace Deguffer.App.Shell;

/// <summary>
/// What became of a change asked of a stored setting.
///
/// <para>Unchanged is its own answer rather than a save, because a control that is put back after a
/// refused write reports the value it was put back to, and that value is the one already stored. Read
/// as a save, it told the page the folder had taken a write a moment after the folder refused one, and
/// the notice saying so closed before anyone could read it.</para>
/// </summary>
public enum SaveOutcome
{
    /// <summary>The change was written to disk and now holds.</summary>
    Saved,

    /// <summary>The change asked for what already holds, so nothing was written.</summary>
    Unchanged,

    /// <summary>The write failed, so nothing changed, on disk or in memory.</summary>
    Refused,
}
