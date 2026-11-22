namespace Deguffer.Core.Choosing;

/// <summary>
/// One of the Storage page's selection menu entries: a change to the ticks of every row at once.
/// What each does to one row is <see cref="BulkSelectionRule"/>'s answer.
/// </summary>
public enum BulkSelection
{
    /// <summary>
    /// Tick every row on screen with bytes to give back and no tick yet, and leave the rest as they are.
    /// </summary>
    All,

    /// <summary>
    /// Of the rows on screen, tick the ones §3's "Default" column ticks on a first scan, and clear every
    /// other row. A recommended row that already has a tick keeps the items the reader chose in it.
    /// </summary>
    Recommended,

    /// <summary>Clear every row.</summary>
    None,
}

/// <summary>What a <see cref="BulkSelection"/> does to one row.</summary>
public enum RowTick
{
    /// <summary>The row keeps the ticks it has.</summary>
    Leave,

    /// <summary>Every step in the row that can be ticked is ticked, as a click on the row's clear checkbox does.</summary>
    Tick,

    /// <summary>Every step in the row is cleared.</summary>
    Clear,
}
