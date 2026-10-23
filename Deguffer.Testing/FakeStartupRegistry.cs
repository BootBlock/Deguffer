using Deguffer.Core.Safety;
using Deguffer.Core.Startup;
using Microsoft.Win32;

namespace Deguffer.Testing;

/// <summary>
/// The <c>Run</c> keys and the <c>StartupApproved</c> keys as a test writes them, one approval key
/// per source as Windows has them. It records every write, so a test can hold §7.5's rule that a
/// change writes one value and nothing else.
/// </summary>
public sealed class FakeStartupRegistry : IStartupRegistry
{
    private readonly Dictionary<StartupSource, Dictionary<string, StoredValue>> _run = [];
    private readonly Dictionary<StartupSource, Dictionary<string, StoredValue>> _approvals = [];
    private readonly HashSet<StartupSource> _refusedRun = [];
    private readonly HashSet<StartupSource> _refusedApprovals = [];
    private readonly HashSet<StartupSource> _refusedWrites = [];
    private readonly List<(StartupSource Source, string Name, StoredValue Value)> _writes = [];

    /// <summary>Every value written, in order.</summary>
    public IReadOnlyList<(StartupSource Source, string Name, StoredValue Value)> Writes => _writes;

    /// <summary>
    /// Run after each write, so a test can change the registry the way something else on the machine
    /// would while the change runs.
    /// </summary>
    public Action<FakeStartupRegistry>? AfterWrite { get; set; }

    /// <summary>A <c>REG_SZ</c> command under <paramref name="source"/>'s <c>Run</c> key.</summary>
    public FakeStartupRegistry WithRun(StartupSource source, string name, string command, RegistryValueKind kind = RegistryValueKind.String)
    {
        Values(_run, source)[name] = new StoredValue(kind, command);
        return this;
    }

    public FakeStartupRegistry WithRunValue(StartupSource source, string name, StoredValue value)
    {
        Values(_run, source)[name] = value;
        return this;
    }

    /// <summary>A <c>StartupApproved</c> value of the measured shape: <paramref name="state"/> and a FILETIME.</summary>
    public FakeStartupRegistry WithApproval(StartupSource source, string name, uint state, long time = 0)
    {
        var bytes = new byte[Approval.Length];
        BitConverter.GetBytes(state).CopyTo(bytes, 0);
        BitConverter.GetBytes(time).CopyTo(bytes, 4);

        return WithApprovalValue(source, name, new StoredValue(RegistryValueKind.Binary, bytes));
    }

    public FakeStartupRegistry WithApprovalValue(StartupSource source, string name, StoredValue value)
    {
        Values(_approvals, source)[name] = value;
        return this;
    }

    public FakeStartupRegistry RefusingRun(StartupSource source)
    {
        _refusedRun.Add(source);
        return this;
    }

    public FakeStartupRegistry RefusingApprovals(StartupSource source)
    {
        _refusedApprovals.Add(source);
        return this;
    }

    /// <summary>Windows refuses a write here, as it does to an unelevated process under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public FakeStartupRegistry RefusingWrites(StartupSource source)
    {
        _refusedWrites.Add(source);
        return this;
    }

    /// <summary>The <c>StartupApproved</c> value as it stands, or null.</summary>
    public StoredValue? ApprovalOf(StartupSource source, string name) =>
        _approvals.TryGetValue(source, out var values) && values.TryGetValue(name, out var value) ? value : null;

    public KeyValues ReadRun(StartupSource source) =>
        _refusedRun.Contains(source) ? KeyValues.Refused : Snapshot(_run, source);

    public KeyValues ReadApprovals(StartupSource source) =>
        _refusedApprovals.Contains(source) ? KeyValues.Refused : Snapshot(_approvals, source);

    public void WriteApproval(StartupSource source, string name, StoredValue value)
    {
        if (_refusedWrites.Contains(source))
        {
            throw new UnauthorizedAccessException("Access to the registry key is denied.");
        }

        _writes.Add((source, name, value));
        Values(_approvals, source)[name] = value;
        AfterWrite?.Invoke(this);
    }

    private static Dictionary<string, StoredValue> Values(Dictionary<StartupSource, Dictionary<string, StoredValue>> keys, StartupSource source)
    {
        if (!keys.TryGetValue(source, out var values))
        {
            values = new Dictionary<string, StoredValue>(StringComparer.OrdinalIgnoreCase);
            keys[source] = values;
        }

        return values;
    }

    /// <summary>A copy, so what a caller holds does not change under it when the fake is written.</summary>
    private static KeyValues Snapshot(Dictionary<StartupSource, Dictionary<string, StoredValue>> keys, StartupSource source) =>
        keys.TryGetValue(source, out var values)
            ? new KeyValues(PathPresence.Present, new Dictionary<string, StoredValue>(values, StringComparer.OrdinalIgnoreCase))
            : KeyValues.Absent;
}
