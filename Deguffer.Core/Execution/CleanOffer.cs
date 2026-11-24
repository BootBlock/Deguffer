using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What the Storage page's Clean button says: the action, and what the ticked steps would give back.
///
/// <para>The figure is on the button rather than in a label of its own across the bar, so the amount
/// the user is about to authorise and the press that authorises it read as one decision. It is the
/// same figure the preview's sentence states as selected, formatted by <see cref="FreeSpace"/>, so the
/// two cannot come to disagree.</para>
/// </summary>
public static class CleanOffer
{
    /// <summary>
    /// "Clean" followed by <paramref name="selected"/>, or "Clean" alone where it frees nothing. A
    /// button reading "Clean 0 B" offers to do nothing, and nothing ticked is what leaves it so.
    /// </summary>
    /// <param name="selected">What the ticked steps would give back.</param>
    public static string Label(ScanSize selected) =>
        selected.FreesSomething ? $"Clean {FreeSpace.Format(selected)}" : "Clean";
}
