using System.Security;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Startup;

/// <summary>Every value under one key, by name, as stored.</summary>
/// <param name="Presence">
/// Whether the key is there. <see cref="PathPresence.Refused"/> where Windows would not open it, which
/// is not the same answer as a key with nothing in it.
/// </param>
public sealed record KeyValues(PathPresence Presence, IReadOnlyDictionary<string, StoredValue> Values)
{
    public static KeyValues Absent { get; } = new(PathPresence.Absent, new Dictionary<string, StoredValue>());

    public static KeyValues Refused { get; } = new(PathPresence.Refused, new Dictionary<string, StoredValue>());

    /// <summary>The value named <paramref name="name"/>, or null. Registry names compare without case.</summary>
    public StoredValue? Find(string name) => Values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The <c>Run</c> keys and the <c>StartupApproved</c> keys beside them, behind a seam so §7.5's
/// rules are proved against values a test wrote rather than the machine's (G1).
/// </summary>
public interface IStartupRegistry
{
    /// <summary>Every value under one <c>Run</c> key. Never throws for a refusal.</summary>
    KeyValues ReadRun(StartupSource source);

    /// <summary>Every value under the <c>StartupApproved</c> key an entry from <paramref name="source"/> is recorded in.</summary>
    KeyValues ReadApprovals(StartupSource source);

    /// <summary>
    /// Write one <c>StartupApproved</c> value, creating its key where it is not there yet, and
    /// nothing else. Throws <see cref="UnauthorizedAccessException"/>, <see cref="SecurityException"/>
    /// or <see cref="IOException"/> where Windows refuses.
    /// </summary>
    void WriteApproval(StartupSource source, string name, StoredValue value);
}

/// <inheritdoc />
public sealed class WindowsStartupRegistry : IStartupRegistry
{
    public static WindowsStartupRegistry Default { get; } = new(OpenRun, OpenApprovals);

    private readonly Func<StartupSource, RegistryKey?> _openRun;
    private readonly Func<StartupSource, bool, RegistryKey?> _openApprovals;

    /// <param name="openRun">Opens a source's <c>Run</c> key to read, or returns null where it is not there.</param>
    /// <param name="openApprovals">
    /// Opens a source's <c>StartupApproved</c> key, creating it when asked to write, or returns null
    /// where it is not there. A test points both at scratch keys under <c>HKEY_CURRENT_USER</c>.
    /// </param>
    internal WindowsStartupRegistry(
        Func<StartupSource, RegistryKey?> openRun,
        Func<StartupSource, bool, RegistryKey?> openApprovals)
    {
        _openRun = openRun;
        _openApprovals = openApprovals;
    }

    public KeyValues ReadRun(StartupSource source) => Read(() => _openRun(source));

    public KeyValues ReadApprovals(StartupSource source) => Read(() => _openApprovals(source, false));

    public void WriteApproval(StartupSource source, string name, StoredValue value)
    {
        using var key = _openApprovals(source, true)
            ?? throw new IOException($"Windows would not create {source.ApprovalLocation()}.");

        key.SetValue(name, value.Data, value.Kind);
    }

    private static KeyValues Read(Func<RegistryKey?> open)
    {
        try
        {
            using var key = open();

            if (key is null)
            {
                return KeyValues.Absent;
            }

            var values = new Dictionary<string, StoredValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in key.GetValueNames())
            {
                // Unexpanded, so a REG_EXPAND_SZ compares as it is stored: what Windows runs is the
                // expansion, and the entry reader expands it for that.
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } data)
                {
                    values[name] = new StoredValue(key.GetValueKind(name), data);
                }
            }

            return new KeyValues(PathPresence.Present, values);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return KeyValues.Refused;
        }
    }

    private static RegistryKey? OpenRun(StartupSource source)
    {
        var (hive, view) = source switch
        {
            StartupSource.UserRun => (RegistryHive.CurrentUser, RegistryView.Registry64),
            StartupSource.MachineRun => (RegistryHive.LocalMachine, RegistryView.Registry64),
            StartupSource.MachineRun32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a Run key."),
        };

        // A predefined key: disposing it closes nothing, so the subkey outlives it safely.
        using var root = RegistryKey.OpenBaseKey(hive, view);

        return root.OpenSubKey(StartupSources.RunKeyPath);
    }

    /// <summary>
    /// Always the 64-bit view: Windows keeps the 32-bit <c>Run</c> key's values under <c>Run32</c>
    /// rather than in a second view of <c>StartupApproved</c>.
    /// </summary>
    private static RegistryKey? OpenApprovals(StartupSource source, bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(source.ApprovalHive(), RegistryView.Registry64);
        var path = StartupSources.ApprovedKeyPath + @"\" + source.ApprovalSubkey();

        return writable ? root.CreateSubKey(path, writable: true) : root.OpenSubKey(path);
    }
}
