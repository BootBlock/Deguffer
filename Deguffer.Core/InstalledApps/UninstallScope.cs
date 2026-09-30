namespace Deguffer.Core.InstalledApps;

/// <summary>
/// One of the three <c>Uninstall</c> keys Windows lists installed programs from (§7.3).
///
/// <para>The machine-wide key is two keys, because a 32-bit installer writes it in the 32-bit
/// registry view. <c>HKEY_CURRENT_USER\Software</c> is shared between the views, so the per-user key
/// is one.</para>
/// </summary>
public enum UninstallScope
{
    /// <summary><c>HKEY_LOCAL_MACHINE</c>, 64-bit view.</summary>
    Machine64,

    /// <summary><c>HKEY_LOCAL_MACHINE</c>, 32-bit view, which is <c>WOW6432Node</c> on disk.</summary>
    Machine32,

    /// <summary><c>HKEY_CURRENT_USER</c>.</summary>
    CurrentUser,
}

/// <summary>What each <see cref="UninstallScope"/> is, for the reader and for the words shown.</summary>
public static class UninstallScopes
{
    /// <summary>The key every scope names, relative to its hive and view.</summary>
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Every scope, in the order the page reads them.</summary>
    public static IReadOnlyList<UninstallScope> All { get; } =
        [UninstallScope.Machine64, UninstallScope.Machine32, UninstallScope.CurrentUser];

    /// <summary>
    /// Whether changing an entry in <paramref name="scope"/> needs administrator rights. The
    /// machine-wide key grants <c>Users</c> read access only.
    /// </summary>
    public static bool NeedsAdministrator(this UninstallScope scope) => scope is not UninstallScope.CurrentUser;

    /// <summary>
    /// The key's full path as it is stored, in the 64-bit view: the 32-bit view's key is named by its
    /// <c>WOW6432Node</c> path. A backup names this path so the file restores to the key it came from
    /// whichever view imports it.
    /// </summary>
    public static string PhysicalPath(this UninstallScope scope) => scope switch
    {
        UninstallScope.Machine64 => @"HKEY_LOCAL_MACHINE\" + KeyPath,
        UninstallScope.Machine32 => @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        UninstallScope.CurrentUser => @"HKEY_CURRENT_USER\" + KeyPath,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>Who the entry is for, in the user's words.</summary>
    public static string Describe(this UninstallScope scope) => scope switch
    {
        UninstallScope.Machine64 => "All users",
        UninstallScope.Machine32 => "All users (32-bit)",
        UninstallScope.CurrentUser => "This user",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };
}
