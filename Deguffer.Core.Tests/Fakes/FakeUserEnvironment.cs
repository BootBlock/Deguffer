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

    public FakeUserEnvironment(string root)
    {
        UserProfile = Path.Combine(root, "profile");
        LocalAppData = Path.Combine(root, "profile", "AppData", "Local");
        RoamingAppData = Path.Combine(root, "profile", "AppData", "Roaming");
        TempPath = Path.Combine(root, "temp");

        Directory.CreateDirectory(UserProfile);
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(RoamingAppData);
        Directory.CreateDirectory(TempPath);
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string TempPath { get; }

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

    public string? GetEnvironmentVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : null;

    public int InvalidateCount { get; private set; }

    public string? FindExecutable(string command) =>
        _executables.TryGetValue(command, out var path) ? path : null;

    public void Invalidate() => InvalidateCount++;
}
