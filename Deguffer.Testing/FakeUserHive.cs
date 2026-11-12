using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// One account's registry hive, held in memory, so a provider's reading of a user's settings can be
/// asserted without the developer's own registry.
/// </summary>
public sealed class FakeUserHive : IUserHive
{
    // Holds a string, an int or bytes, as the registry holds REG_SZ, REG_DWORD and REG_BINARY, so a
    // read of the wrong type is told nothing, as it would be on a real machine.
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a provider read this hive. Proves a discovery is memoised.</summary>
    public int Reads { get; private set; }

    /// <summary>Pretend <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserHive WithValue(string keyPath, string valueName, string value) => With(keyPath, valueName, value);

    /// <summary>Pretend the <c>REG_DWORD</c> <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserHive WithNumber(string keyPath, string valueName, int value) => With(keyPath, valueName, value);

    /// <summary>Pretend the <c>REG_BINARY</c> <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserHive WithBytes(string keyPath, string valueName, byte[] value) => With(keyPath, valueName, value);

    public string? ReadValue(string keyPath, string valueName) => Read(keyPath, valueName) as string;

    public int? ReadNumber(string keyPath, string valueName) => Read(keyPath, valueName) is int number ? number : null;

    public byte[]? ReadBytes(string keyPath, string valueName) => Read(keyPath, valueName) as byte[];

    /// <summary>
    /// Every key a recorded value sits under, one level below <paramref name="keyPath"/>. A value
    /// recorded directly under <paramref name="keyPath"/> is not a key, so it names nothing here.
    /// </summary>
    public IReadOnlyList<string> ReadSubKeyNames(string keyPath)
    {
        Reads++;

        return
        [
            .. Below(keyPath)
                .Select(entry => entry.Split('\\'))
                .Where(segments => segments.Length > 1)
                .Select(segments => segments[0])
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Every value recorded directly under <paramref name="keyPath"/>. One recorded under a key below
    /// it is that key's, so it names nothing here.
    /// </summary>
    public IReadOnlyList<string> ReadValueNames(string keyPath)
    {
        Reads++;

        return [.. Below(keyPath).Where(name => !name.Contains('\\', StringComparison.Ordinal))];
    }

    private FakeUserHive With(string keyPath, string valueName, object value)
    {
        _values[keyPath + "\\" + valueName] = value;
        return this;
    }

    private object? Read(string keyPath, string valueName)
    {
        Reads++;

        return _values.GetValueOrDefault(keyPath + "\\" + valueName);
    }

    private IEnumerable<string> Below(string keyPath)
    {
        var prefix = keyPath + "\\";

        return _values.Keys
            .Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry[prefix.Length..]);
    }
}
