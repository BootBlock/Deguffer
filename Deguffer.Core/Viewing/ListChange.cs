namespace Deguffer.Core.Viewing;

/// <summary>What one change a bound list reports did to its items.</summary>
public enum ListChange
{
    /// <summary>Put an item into the list.</summary>
    Inserted,

    /// <summary>Moved an item to another place in the list, which still holds it.</summary>
    Moved,

    /// <summary>Took an item out of the list.</summary>
    Removed,

    /// <summary>Put one item in the place of another, which is no longer in the list.</summary>
    Replaced,

    /// <summary>Changed the items in a way the list does not describe, so any item may have gone.</summary>
    Reset,
}
