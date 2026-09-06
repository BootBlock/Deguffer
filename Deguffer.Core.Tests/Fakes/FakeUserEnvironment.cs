using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A profile rooted in a temp directory, so provider rules can be asserted against a tree we
/// build rather than the developer's real caches.
/// </summary>
public sealed class FakeUserEnvironment : IUserEnvironment
{
    private readonly Dictionary<string, string> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registry = new(StringComparer.OrdinalIgnoreCase);

    public FakeUserEnvironment(string root)
    {
        UserProfile = Path.Combine(root, "profile");
        LocalAppData = Path.Combine(root, "profile", "AppData", "Local");
        RoamingAppData = Path.Combine(root, "profile", "AppData", "Roaming");
        LocalLowAppData = Path.Combine(root, "profile", "AppData", "LocalLow");
        TempPath = Path.Combine(root, "temp");

        Directory.CreateDirectory(UserProfile);
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(RoamingAppData);
        Directory.CreateDirectory(LocalLowAppData);
        Directory.CreateDirectory(TempPath);
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string? LocalLowAppData { get; private set; }

    public string TempPath { get; }

    /// <summary>
    /// A recognisably invented identifier. Real enough in shape for a provider that matches on it,
    /// and nobody's actual SID.
    ///
    /// <para>A constant as well as the default, because a fake standing in for something that acts
    /// on this account's own directory has to name the same account the environment does. Two
    /// copies of the literal would agree until one of them was edited.</para>
    /// </summary>
    public const string SecurityIdentifier = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public string? UserSecurityIdentifier { get; private set; } = SecurityIdentifier;

    /// <summary>
    /// A recognisably invented account name and machine name, on the same terms as
    /// <see cref="SecurityIdentifier"/>: a fixture that laid out a File History target has to name
    /// the same account and the same machine the provider will look for.
    /// </summary>
    public const string Account = "testuser";

    public const string Machine = "TESTMACHINE";

    public string UserName { get; } = Account;

    public string MachineName { get; } = Machine;

    /// <summary>
    /// Pretend the account is unidentifiable, which is how a provider that keys on the SID is shown
    /// to fail closed. Set before the provider is constructed: providers read the identity once,
    /// because a process cannot change the account it runs as.
    /// </summary>
    public FakeUserEnvironment WithNoSecurityIdentifier()
    {
        UserSecurityIdentifier = null;
        return this;
    }

    /// <summary>
    /// Pretend Windows would not say where LocalLow is, which is how a provider that reaches into
    /// it is shown to fail closed. The directory itself is left on disk, so the test can prove the
    /// provider declined a cache that was really there rather than one that was simply absent.
    /// </summary>
    public FakeUserEnvironment WithNoLocalLow()
    {
        LocalLowAppData = null;
        return this;
    }

    /// <summary>Pretend <paramref name="command"/> is installed at a plausible path.</summary>
    public FakeUserEnvironment WithExecutable(string command, string? path = null)
    {
        _executables[command] = path ?? Path.Combine(@"C:\tools", command + ".exe");
        return this;
    }

    /// <summary>Pretend <paramref name="name"/> is set in the environment.</summary>
    public FakeUserEnvironment WithEnvironmentVariable(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    /// <summary>Pretend <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserEnvironment WithRegistryValue(string keyPath, string valueName, string value)
    {
        _registry[keyPath + "\\" + valueName] = value;
        return this;
    }

    /// <summary>How many times a provider read the environment. Proves a discovery is memoised.</summary>
    public int EnvironmentReads { get; private set; }

    /// <summary>How many times a provider read the registry. Proves a discovery is memoised.</summary>
    public int RegistryReads { get; private set; }

    public string? ReadCurrentUserRegistryValue(string keyPath, string valueName)
    {
        RegistryReads++;

        return _registry.TryGetValue(keyPath + "\\" + valueName, out var value) ? value : null;
    }

    public string? GetEnvironmentVariable(string name)
    {
        EnvironmentReads++;

        return _variables.TryGetValue(name, out var value) ? value : null;
    }

    public int InvalidateCount { get; private set; }

    public string? FindExecutable(string command) =>
        _executables.TryGetValue(command, out var path) ? path : null;

    public void Invalidate() => InvalidateCount++;
}
