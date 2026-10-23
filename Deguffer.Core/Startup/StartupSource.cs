using Microsoft.Win32;

namespace Deguffer.Core.Startup;

/// <summary>
/// One of the six places Windows starts programs from at sign-in that Task Manager's Startup apps
/// page lists (§7.5). <c>RunOnce</c>, the policy <c>Run</c> keys, Winlogon, scheduled tasks and
/// services are not among them, and nothing here reads them.
/// </summary>
public enum StartupSource
{
    /// <summary>This user's <c>Run</c> key.</summary>
    UserRun,

    /// <summary>The machine's <c>Run</c> key, 64-bit view.</summary>
    MachineRun,

    /// <summary>The machine's <c>Run</c> key, 32-bit view, which is <c>WOW6432Node</c> on disk.</summary>
    MachineRun32,

    /// <summary>This user's Startup folder.</summary>
    UserStartupFolder,

    /// <summary>The Startup folder every user shares.</summary>
    CommonStartupFolder,

    /// <summary>A startup task a packaged application installed for this user declares.</summary>
    PackagedTask,
}

/// <summary>What each <see cref="StartupSource"/> is, for the reader and for the words shown.</summary>
public static class StartupSources
{
    /// <summary>The <c>Run</c> key, relative to its hive and view.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager and Settings record the user's choice, relative to the entry's hive.</summary>
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>The <c>Run</c> keys, in the order the page reads them.</summary>
    public static IReadOnlyList<StartupSource> RunKeys { get; } =
        [StartupSource.UserRun, StartupSource.MachineRun, StartupSource.MachineRun32];

    /// <summary>The Startup folders, in the order the page reads them.</summary>
    public static IReadOnlyList<StartupSource> Folders { get; } =
        [StartupSource.UserStartupFolder, StartupSource.CommonStartupFolder];

    /// <summary>
    /// Whether changing an entry from <paramref name="source"/> needs administrator rights. The
    /// machine's keys and the shared Startup folder's record grant <c>Users</c> read access only.
    /// </summary>
    public static bool NeedsAdministrator(this StartupSource source) =>
        source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonStartupFolder;

    /// <summary>
    /// The hive an entry's <c>StartupApproved</c> value is in. The same hive as the entry: Windows
    /// records a machine-wide entry's state for every user, as Task Manager shows it.
    /// </summary>
    public static RegistryHive ApprovalHive(this StartupSource source) => source switch
    {
        StartupSource.UserRun or StartupSource.UserStartupFolder => RegistryHive.CurrentUser,
        StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonStartupFolder => RegistryHive.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "A packaged task has no StartupApproved value."),
    };

    /// <summary>
    /// The subkey of <see cref="ApprovedKeyPath"/> that holds an entry's value: <c>Run32</c> for the
    /// 32-bit view's <c>Run</c> key, which is how Windows keeps the two views' entries of one name
    /// apart, <c>Run</c> for the other two, and <c>StartupFolder</c> for both folders.
    /// </summary>
    public static string ApprovalSubkey(this StartupSource source) => source switch
    {
        StartupSource.UserRun or StartupSource.MachineRun => "Run",
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserStartupFolder or StartupSource.CommonStartupFolder => "StartupFolder",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "A packaged task has no StartupApproved value."),
    };

    /// <summary>Where the entry comes from, in the user's words.</summary>
    public static string Describe(this StartupSource source) => source switch
    {
        StartupSource.UserRun => "Registry, this user",
        StartupSource.MachineRun => "Registry, all users",
        StartupSource.MachineRun32 => "Registry, all users (32-bit)",
        StartupSource.UserStartupFolder => "Startup folder, this user",
        StartupSource.CommonStartupFolder => "Startup folder, all users",
        StartupSource.PackagedTask => "Packaged app",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>A <c>Run</c> key's full path, for a reader who wants to find it.</summary>
    public static string RunLocation(this StartupSource source) => source switch
    {
        StartupSource.UserRun => @"HKEY_CURRENT_USER\" + RunKeyPath,
        StartupSource.MachineRun => @"HKEY_LOCAL_MACHINE\" + RunKeyPath,
        StartupSource.MachineRun32 => @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a Run key."),
    };

    /// <summary>The <c>StartupApproved</c> key that holds an entry's value, as its full path.</summary>
    public static string ApprovalLocation(this StartupSource source) =>
        (source.ApprovalHive() is RegistryHive.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\")
        + ApprovedKeyPath + @"\" + source.ApprovalSubkey();
}
