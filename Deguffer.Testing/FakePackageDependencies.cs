using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>Package registrations a test writes, each naming the bundles it lists as dependents.</summary>
public sealed class FakePackageDependencies : IPackageDependencies
{
    private readonly Dictionary<string, List<PackageProvider>> _byDependent = new(StringComparer.OrdinalIgnoreCase);
    private bool _refused;

    /// <summary>How many times the registrations were read, for a test that counts.</summary>
    public int Reads { get; private set; }

    /// <summary>A provider named <paramref name="key"/> with <paramref name="code"/> as its default value, listing <paramref name="dependents"/>.</summary>
    public FakePackageDependencies Provider(string key, string? code, params string[] dependents)
    {
        foreach (var dependent in dependents)
        {
            if (!_byDependent.TryGetValue(dependent, out var providers))
            {
                _byDependent[dependent] = providers = [];
            }

            providers.Add(new PackageProvider(key, code));
        }

        return this;
    }

    /// <summary>A provider Windows would not open, which may list any bundle.</summary>
    public FakePackageDependencies Refusing()
    {
        _refused = true;
        return this;
    }

    public PackageDependencies Read()
    {
        Reads++;

        return new PackageDependencies(
            _refused ? PathPresence.Refused : PathPresence.Present,
            _byDependent.ToDictionary(
                pair => pair.Key,
                IReadOnlyList<PackageProvider> (pair) => [.. pair.Value],
                StringComparer.OrdinalIgnoreCase));
    }
}
