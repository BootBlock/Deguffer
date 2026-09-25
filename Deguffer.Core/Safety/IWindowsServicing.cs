using System.Security;
using Microsoft.Win32;

namespace Deguffer.Core.Safety;

/// <summary>
/// Where Windows is in servicing itself: whether an update is waiting for a restart to finish, what
/// that restart will move or delete, and how long an upgrade can still be undone.
///
/// <para>A seam of its own rather than a member on <see cref="ISystemDirectories"/>, which is where
/// Windows is installed, and not what it is in the middle of. The directories an upgrade leaves at the
/// top of the system volume are safe to remove only once the update that wrote them has finished, and
/// that proof has to run on a machine a test can put in the middle of an update.</para>
/// </summary>
public interface IWindowsServicing
{
    /// <summary>
    /// Whether the servicing stack or Windows Update is waiting for a restart to finish what it
    /// started. An unreadable answer counts as waiting, because the other reading deletes.
    /// </summary>
    bool IsRestartPending { get; }

    /// <summary>
    /// Whether a restart will rename or delete something inside <paramref name="directory"/>, or the
    /// list of what it will could not be read.
    ///
    /// <para>Asked per folder rather than as a yes or no for the machine, because Windows keeps this
    /// list for anything that could not be replaced while it was in use, and an installer or an
    /// antivirus product can leave an entry in it on a healthy machine for weeks. An entry inside the
    /// folder is the one that says the folder is still in use by what wrote it.</para>
    /// </summary>
    bool HasPendingOperationsIn(string directory);

    /// <summary>
    /// How many days after an upgrade Windows still offers to go back to the previous version, and
    /// keeps what that needs.
    /// </summary>
    int UninstallWindowDays { get; }
}

/// <inheritdoc />
public sealed class WindowsServicing : IWindowsServicing
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static readonly WindowsServicing Current = new();

    /// <summary>
    /// What <c>DISM /Set-OSUninstallWindow</c> documents: a value from 2 to 60 days, and 10 for
    /// anything else, which is also what a machine that has never been set runs with.
    /// </summary>
    private const int DefaultUninstallWindowDays = 10;

    private const int ShortestUninstallWindowDays = 2;

    private const int LongestUninstallWindowDays = 60;

    /// <summary>
    /// The keys whose presence means a restart is owed. The first two are the ones Microsoft's own
    /// Configuration Manager checks for a pending restart. The other two are the servicing stack's
    /// states for a restart under way and for packages still to be installed by one, which are known
    /// by convention rather than documented, and asking them can only hold more back.
    /// </summary>
    private static readonly string[] RestartKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootInProgress",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending",
    ];

    private const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    private WindowsServicing()
    {
    }

    /// <summary>
    /// Read each time it is asked, because a restart clears it and an update sets it while Deguffer is
    /// open, and a remembered answer would be wrong in the direction that deletes.
    /// </summary>
    public bool IsRestartPending
    {
        get
        {
            using var machine = Machine();

            return RestartKeys.Any(path => Exists(machine, path));
        }
    }

    public bool HasPendingOperationsIn(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        using var machine = Machine();

        try
        {
            using var key = machine.OpenSubKey(SessionManager);

            var within = LongPath.Display(directory);

            return Operations(key?.GetValue("PendingFileRenameOperations"))
                .Concat(Operations(key?.GetValue("PendingFileRenameOperations2")))
                .Any(path => LongPath.Contains(within, path));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            // A list that cannot be read says nothing about where the pending operations are, and the
            // reading that deletes is "none of them are here".
            return true;
        }
    }

    /// <summary>
    /// From <c>HKLM\SYSTEM\Setup</c>'s <c>UninstallWindow</c>, which is where DISM's setting is kept.
    /// That location is not documented, which is why a value outside DISM's documented range is read
    /// as the documented default rather than trusted.
    /// </summary>
    public int UninstallWindowDays
    {
        get
        {
            using var machine = Machine();

            try
            {
                using var setup = machine.OpenSubKey(@"SYSTEM\Setup");

                return setup?.GetValue("UninstallWindow") is int days
                    && days is >= ShortestUninstallWindowDays and <= LongestUninstallWindowDays
                    ? days
                    : DefaultUninstallWindowDays;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
            {
                // The longest window is the one that holds a previous installation back longest.
                return LongestUninstallWindowDays;
            }
        }
    }

    /// <summary>
    /// The 64-bit view on 64-bit Windows, whatever this process is. A 32-bit process is otherwise
    /// shown <c>WOW6432Node</c>, where none of these keys is, and would read every restart as not
    /// pending.
    /// </summary>
    private static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static bool Exists(RegistryKey machine, string path)
    {
        try
        {
            using var key = machine.OpenSubKey(path);

            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            // A key that exists and refuses to be read is still a key that exists.
            return true;
        }
    }

    /// <summary>
    /// The paths in one pending-operations value: pairs of source and destination, each in the NT form
    /// <c>\??\C:\...</c>, a destination prefixed <c>!</c> to replace an existing file, and an empty
    /// destination for a delete.
    /// </summary>
    internal static IEnumerable<string> Operations(object? value) =>
        (value as string[] ?? [])
            .Select(entry => entry.TrimStart('!'))
            .Where(entry => entry.Length > 0)
            .Select(entry => entry.StartsWith(@"\??\", StringComparison.Ordinal) ? entry[4..] : entry)
            .Where(Path.IsPathFullyQualified)
            .Select(LongPath.Display);
}
