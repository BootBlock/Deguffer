using System.Security;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.InstalledApps;

/// <summary>One entry as it was read.</summary>
/// <param name="IsReadable">
/// False where the entry's key is listed but would not open. The entry is still reported, because a
/// key that exists and says nothing is not the same as no key (§7.3).
/// </param>
public sealed record UninstallRecord(UninstallKey Key, UninstallValues Values, bool IsReadable = true);

/// <summary>One <c>Uninstall</c> key as it was read.</summary>
/// <param name="Presence">
/// Whether the key itself is there. <see cref="PathPresence.Refused"/> where Windows would not open
/// it, which is not the same answer as a machine with no per-user entries.
/// </param>
public sealed record UninstallRead(UninstallScope Scope, PathPresence Presence, IReadOnlyList<UninstallRecord> Records);

/// <summary>
/// The <c>Uninstall</c> keys, behind a seam so the rules that decide what is stale and what is
/// removed are proved against entries a test wrote rather than the machine's (G1).
/// </summary>
public interface IUninstallRegistry
{
    /// <summary>Every entry under one <c>Uninstall</c> key. Never throws for a refusal.</summary>
    UninstallRead Read(UninstallScope scope);

    /// <summary>
    /// One entry, read again. <see cref="PathPresence.Absent"/> only where Windows says the key is
    /// not there.
    /// </summary>
    (PathPresence Presence, UninstallValues Values) ReadOne(UninstallKey key);

    /// <summary>
    /// Delete one entry's key with the keys below it, and nothing else. Throws
    /// <see cref="UnauthorizedAccessException"/>, <see cref="SecurityException"/> or
    /// <see cref="IOException"/> where Windows refuses.
    /// </summary>
    void Delete(UninstallKey key);
}

/// <inheritdoc />
public sealed class WindowsUninstallRegistry : IUninstallRegistry
{
    public static WindowsUninstallRegistry Default { get; } = new(OpenParent);

    private readonly Func<UninstallScope, bool, RegistryKey?> _openParent;

    /// <param name="openParent">
    /// Opens the <c>Uninstall</c> key for a scope, writable when asked, or returns null where it is
    /// not there. A test points this at a scratch key under <c>HKEY_CURRENT_USER</c>.
    /// </param>
    internal WindowsUninstallRegistry(Func<UninstallScope, bool, RegistryKey?> openParent) => _openParent = openParent;

    public UninstallRead Read(UninstallScope scope)
    {
        RegistryKey? parent;

        try
        {
            parent = _openParent(scope, false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new UninstallRead(scope, PathPresence.Refused, []);
        }

        if (parent is null)
        {
            return new UninstallRead(scope, PathPresence.Absent, []);
        }

        using (parent)
        {
            string[] names;

            try
            {
                names = parent.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return new UninstallRead(scope, PathPresence.Refused, []);
            }

            var records = new List<UninstallRecord>(names.Length);

            foreach (var name in names)
            {
                var key = new UninstallKey(scope, name);
                var (presence, values) = ReadValues(parent, name);

                // A key deleted between the listing and the read is gone, and reporting it would
                // describe an entry nobody can find.
                if (presence is not PathPresence.Absent)
                {
                    records.Add(new UninstallRecord(key, values, IsReadable: presence is PathPresence.Present));
                }
            }

            return new UninstallRead(scope, PathPresence.Present, records);
        }
    }

    public (PathPresence Presence, UninstallValues Values) ReadOne(UninstallKey key)
    {
        try
        {
            using var parent = _openParent(key.Scope, false);

            return parent is null ? (PathPresence.Absent, UninstallValues.None) : ReadValues(parent, key.Name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (PathPresence.Refused, UninstallValues.None);
        }
    }

    public void Delete(UninstallKey key)
    {
        using var parent = _openParent(key.Scope, true)
            ?? throw new IOException($"The key {key.Scope.PhysicalPath()} is not there.");

        // A key already gone is not an error here: the check after the removal reports what is
        // there, and it reads the machine rather than trusting this call.
        parent.DeleteSubKeyTree(key.Name, throwOnMissingSubKey: false);
    }

    private static (PathPresence Presence, UninstallValues Values) ReadValues(RegistryKey parent, string name)
    {
        try
        {
            using var key = parent.OpenSubKey(name);

            if (key is null)
            {
                return (PathPresence.Absent, UninstallValues.None);
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var valueName in key.GetValueNames())
            {
                // GetValue expands REG_EXPAND_SZ against this process's environment, which is what
                // Windows does when it runs the command the value names.
                if (key.GetValue(valueName) is { } value)
                {
                    values[valueName] = value;
                }
            }

            return (PathPresence.Present, new UninstallValues(values));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (PathPresence.Refused, UninstallValues.None);
        }
    }

    private static RegistryKey? OpenParent(UninstallScope scope, bool writable)
    {
        var (hive, view) = scope switch
        {
            UninstallScope.Machine64 => (RegistryHive.LocalMachine, RegistryView.Registry64),
            UninstallScope.Machine32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
            UninstallScope.CurrentUser => (RegistryHive.CurrentUser, RegistryView.Registry64),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        // A predefined key: disposing it closes nothing, so the subkey outlives it safely.
        using var root = RegistryKey.OpenBaseKey(hive, view);

        return root.OpenSubKey(UninstallScopes.KeyPath, writable);
    }
}
