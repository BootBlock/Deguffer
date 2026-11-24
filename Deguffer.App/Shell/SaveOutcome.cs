namespace Deguffer.App.Shell;

/// <summary>
/// What became of a change asked of a stored setting.
///
/// <para>A change that wrote nothing is its own answer rather than a save, because it says nothing about
/// whether the folder can be written. A control put back after a refused write sends the value it was
/// put back to, which is the one already stored, and a page that took that for a save would close the
/// notice of the refusal as it opened.</para>
/// </summary>
public enum SaveOutcome
{
    /// <summary>The change was written to disk and now holds.</summary>
    Saved,

    /// <summary>
    /// Nothing was written and no write failed: the change asked for what already holds, or went to a
    /// store Deguffer does not write while it cannot read it.
    /// </summary>
    NotWritten,

    /// <summary>The write failed, so nothing changed, on disk or in memory.</summary>
    Refused,
}
