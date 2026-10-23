using Deguffer.Core.Startup;

namespace Deguffer.Testing;

/// <summary>Shortcuts as a test describes them, by path, so a Startup folder needs no shell to write one.</summary>
public sealed class FakeShortcutReader : IShortcutReader
{
    private readonly Dictionary<string, ShortcutTarget?> _shortcuts = new(StringComparer.OrdinalIgnoreCase);

    public FakeShortcutReader With(string path, ShortcutTarget? target)
    {
        _shortcuts[path] = target;
        return this;
    }

    public ShortcutTarget? Read(string path) => _shortcuts.TryGetValue(path, out var target) ? target : null;
}
