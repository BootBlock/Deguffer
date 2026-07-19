namespace Deguffer.Core.Scanning;

/// <summary>
/// §7: show free space before and after, prominently. It is the only number the user came for.
/// </summary>
public static class FreeSpace
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>, or null if unknown. Callers
    /// supply the path from <c>IUserEnvironment</c> rather than this class reading the profile
    /// location itself — Core does not touch <c>Environment.GetFolderPath</c>.
    /// </summary>
    public static long? ForPath(string path) => Measure(path, static drive => drive.AvailableFreeSpace);

    /// <summary>
    /// Total size of the volume holding <paramref name="path"/>, or null if unknown. Capacity does
    /// not change while the app is open, so the UI reads it once and keeps it — only the free
    /// figure is re-read after a run.
    /// </summary>
    public static long? TotalForPath(string path) => Measure(path, static drive => drive.TotalSize);

    private static long? Measure(string path, Func<DriveInfo, long> read)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : read(new DriveInfo(root));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unavailable or disconnected volume is not an error worth surfacing; the UI
            // shows a dash instead of a number.
            return null;
        }
    }

    /// <summary>Human-readable size, in the binary units a developer expects.</summary>
    public static string Format(long bytes)
    {
        double value = Math.Abs(bytes);
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        var precision = unit >= 2 && value < 100 ? 1 : 0;

        return $"{sign}{value.ToString($"F{precision}")} {Units[unit]}";
    }
}
