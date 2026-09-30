using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// <c>Uninstall</c> keys a test writes: entries with whatever values it needs, keys that refuse,
/// and entries that change between two reads.
/// </summary>
public sealed class FakeUninstallRegistry : IUninstallRegistry
{
    private readonly Dictionary<UninstallKey, (bool Readable, Dictionary<string, object> Values)> _entries = [];
    private readonly HashSet<UninstallScope> _refusedScopes = [];

    /// <summary>How many times each scope was read, for a test that counts.</summary>
    public Dictionary<UninstallScope, int> Reads { get; } = [];

    /// <summary>Add or replace an entry, and return its key.</summary>
    public UninstallKey With(UninstallScope scope, string name, params (string Name, object Value)[] values)
    {
        var key = new UninstallKey(scope, name);
        _entries[key] = (true, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase));
        return key;
    }

    /// <summary>An entry listed under its key that will not open.</summary>
    public UninstallKey WithUnreadable(UninstallScope scope, string name)
    {
        var key = new UninstallKey(scope, name);
        _entries[key] = (false, []);
        return key;
    }

    /// <summary>A whole <c>Uninstall</c> key Windows will not open.</summary>
    public FakeUninstallRegistry Refusing(UninstallScope scope)
    {
        _refusedScopes.Add(scope);
        return this;
    }

    public void Remove(UninstallKey key) => _entries.Remove(key);

    public UninstallRead Read(UninstallScope scope)
    {
        Reads[scope] = Reads.GetValueOrDefault(scope) + 1;

        if (_refusedScopes.Contains(scope))
        {
            return new UninstallRead(scope, PathPresence.Refused, []);
        }

        return new UninstallRead(
            scope,
            PathPresence.Present,
            [.. _entries.Where(e => e.Key.Scope == scope)
                .Select(e => new UninstallRecord(e.Key, new UninstallValues(e.Value.Values), e.Value.Readable))]);
    }

    public (PathPresence Presence, UninstallValues Values) ReadOne(UninstallKey key) =>
        _refusedScopes.Contains(key.Scope) ? (PathPresence.Refused, UninstallValues.None)
        : !_entries.TryGetValue(key, out var entry) ? (PathPresence.Absent, UninstallValues.None)
        : entry.Readable ? (PathPresence.Present, new UninstallValues(entry.Values))
        : (PathPresence.Refused, UninstallValues.None);
}
