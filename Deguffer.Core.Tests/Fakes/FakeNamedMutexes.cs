using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>Named mutexes that exist exactly where a test says they do.</summary>
public sealed class FakeNamedMutexes(params string[] existing) : INamedMutexes
{
    private readonly HashSet<string> _existing = new(existing, StringComparer.Ordinal);

    public static FakeNamedMutexes None => new();

    public bool Exists(string name) => _existing.Contains(name);
}
