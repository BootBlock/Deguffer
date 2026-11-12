using System.Security;
using Microsoft.Win32;

namespace Deguffer.Core.SystemProtection;

/// <summary>Reads <see cref="PointInTimeRestore"/> from the machine's registry.</summary>
internal static class PointInTimeRestoreCalls
{
    private const string Feature = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\Recovery\PITR";

    private const string Snapshots = Feature + @"\Snapshots";

    public static PointInTimeRestore Read()
    {
        // The 64-bit view whatever this process is: a 32-bit process is otherwise shown WOW6432Node,
        // where the feature keeps nothing, and would read a machine full of its snapshots as having none.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        try
        {
            using var feature = machine.OpenSubKey(Feature);

            if (feature is null)
            {
                return PointInTimeRestore.Absent;
            }

            using var snapshots = machine.OpenSubKey(Snapshots);

            // How the feature lays out one snapshot is not known, so any entry at all counts as one.
            return snapshots is null || snapshots.SubKeyCount + snapshots.ValueCount == 0
                ? PointInTimeRestore.Idle
                : PointInTimeRestore.Keeping;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A refusal is not an absence: the snapshots may be there, and only reading them could say.
            return PointInTimeRestore.Unread(
                $"Windows would not show what point-in-time restore records of its snapshots ({ex.Message.TrimEnd('.')}).");
        }
    }
}
