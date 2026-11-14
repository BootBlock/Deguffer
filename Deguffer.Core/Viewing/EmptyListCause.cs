namespace Deguffer.Core.Viewing;

/// <summary>
/// Why a list shows no rows. Each cause asks the reader for a different next step, so a page draws
/// each one differently rather than one blank list for all three.
/// </summary>
public enum EmptyListCause
{
    /// <summary>There is nothing of the kind to list.</summary>
    Nothing,

    /// <summary>Rows exist, and the reader's filter text matches none of them.</summary>
    Filtered,

    /// <summary>Rows exist, and every one is of a kind the reader chose not to show.</summary>
    Hidden,
}
