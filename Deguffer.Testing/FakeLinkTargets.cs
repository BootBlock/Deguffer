using System.Collections.Concurrent;
using Deguffer.Core.Exploring.Links;

namespace Deguffer.Testing;

/// <summary>
/// Shortcuts and links as a test describes them, by path, so a broken link needs no shell to write a
/// shortcut and no privilege to make a link. A shortcut is read only as an ordinary file and a link
/// only as a reparse point, as the real reader reads each.
/// </summary>
public sealed class FakeLinkTargets : ILinkTargetReader
{
    private readonly ConcurrentDictionary<string, LinkTarget> _links = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times each path was read, for a test that counts.</summary>
    public ConcurrentDictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Say that <paramref name="path"/> is a link of <paramref name="kind"/> to <paramref name="target"/>, in place of whatever it was.</summary>
    public FakeLinkTargets With(string path, LinkKind kind, string target)
    {
        _links[path] = new LinkTarget(kind, target);
        return this;
    }

    public LinkTarget? Read(string path, bool isLink)
    {
        Reads.AddOrUpdate(path, 1, (_, count) => count + 1);

        return _links.TryGetValue(path, out var link) && (link.Kind is LinkKind.Shortcut) != isLink ? link : null;
    }
}
