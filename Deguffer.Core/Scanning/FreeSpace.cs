namespace Deguffer.Core.Scanning;

/// <summary>
/// §7: show free space before and after, prominently. It is the only number the user came for.
/// </summary>
public static class FreeSpace
{
    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null if unknown.</summary>
    public static long? ForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Free bytes on the volume holding the user profile — the drive that matters here.</summary>
    public static long? ForUserProfile() =>
        ForPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Human-readable size, in the binary units a developer expects.</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Abs(bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        var precision = unit >= 2 && value < 100 ? 1 : 0;

        return $"{sign}{value.ToString($"F{precision}")} {units[unit]}";
    }
}
