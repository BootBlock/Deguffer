namespace Deguffer.Core.Health;

/// <summary>Which drive is selected once the list has changed under the reader.</summary>
public static class DriveSelection
{
    /// <summary>
    /// The row at the same place, or the last where the list has become shorter, so the detail pane
    /// is never left describing a drive that has gone. Null where no rows are left.
    /// </summary>
    public static int? Nearest(int previousIndex, int count) =>
        count == 0 ? null : Math.Clamp(previousIndex, 0, count - 1);
}
