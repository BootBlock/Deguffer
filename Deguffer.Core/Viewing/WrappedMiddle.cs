namespace Deguffer.Core.Viewing;

/// <summary>
/// Which line a wrapped row's middle part stays on. See <see cref="RowReflow"/>.
/// </summary>
public enum WrappedMiddle
{
    /// <summary>
    /// Beside the name, on the first line: a status that trims beside it, or an offer to elevate
    /// beside the total it explains.
    /// </summary>
    WithLead = 0,

    /// <summary>
    /// Beside the last part, on the second line: a folder scope that needs more room than a full
    /// first line leaves it, and belongs with the action beside it.
    /// </summary>
    WithTail = 1,
}
