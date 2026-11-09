namespace Deguffer.Core.Health;

/// <summary>Which drive is selected once the list has changed under the reader.</summary>
public static class DriveSelection
{
    /// <summary>
    /// <paramref name="selected"/> while it is still listed. Otherwise the row at the place it held,
    /// or the last where the list has become shorter, so the detail pane is never left describing a
    /// drive that has gone. Null where no rows are left.
    /// </summary>
    /// <param name="previousIndex">Where the selection sat before the list changed, or 0 where nothing was selected.</param>
    public static T? After<T>(IReadOnlyList<T> rows, T? selected, int previousIndex)
        where T : class
    {
        if (selected is not null && rows.Contains(selected))
        {
            return selected;
        }

        return rows.Count == 0 ? null : rows[Math.Clamp(previousIndex, 0, rows.Count - 1)];
    }
}
