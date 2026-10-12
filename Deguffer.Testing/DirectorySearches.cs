using System.Collections.Frozen;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>Searches for a test to hand a scanner or the volume index.</summary>
public static class DirectorySearches
{
    /// <summary>
    /// A search for <paramref name="names"/>, without regard to case, that enters every other
    /// directory. The source discovery's own search also refuses <c>.git</c> and
    /// <c>node_modules</c>, and a test about those asks for that one.
    /// </summary>
    public static DirectorySearch For(params string[] names) =>
        new(FrozenSet.Create(StringComparer.OrdinalIgnoreCase, names), FrozenSet<string>.Empty);
}
