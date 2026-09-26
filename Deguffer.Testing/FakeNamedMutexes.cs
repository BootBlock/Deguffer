using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>Named mutexes that exist exactly where a test says they do.</summary>
public sealed class FakeNamedMutexes(params string[] existing) : INamedMutexes
{
    private readonly HashSet<string> _existing = new(existing, StringComparer.Ordinal);

    public static FakeNamedMutexes None => new();

    /// <summary>
    /// Declare a mutex existing from now on, so a test can have a tool take one between the preview
    /// and the clean and show the clean asking again.
    /// </summary>
    public FakeNamedMutexes WithExisting(string name)
    {
        _existing.Add(name);
        return this;
    }

    public bool Exists(string name) => _existing.Contains(name);
}
