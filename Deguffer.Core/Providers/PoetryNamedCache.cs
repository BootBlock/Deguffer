using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where a cache Poetry names sits inside its repository cache (§5.2).</summary>
internal static class PoetryNamedCache
{
    /// <summary>
    /// Where a cache Poetry named sits, or null when the name does not resolve to a directory
    /// directly inside the repository cache.
    ///
    /// <para>§5.2, and a different question from the one the name itself answers.
    /// <see cref="PoetryDiscovery"/> checks that a name is safe to put in a command line;
    /// this checks that it is safe to put in a path, and the two do not overlap. <c>.</c> and
    /// <c>..</c> are perfectly ordinary command-line tokens, and both walk out of the directory the
    /// step claims to be about: <c>..</c> resolves to <c>{cache-dir}\cache</c>, which this
    /// Poetry row's own §5.6 list says must survive, and Poetry's <c>clear</c> removes the directory
    /// it is handed rather than emptying it. Poetry's own containment check does not catch that
    /// either, because it compares the paths without resolving them.</para>
    ///
    /// <para>Asked as "is the parent exactly the repository cache" rather than as a containment
    /// test, because <see cref="LongPath.Contains"/> counts a directory as containing itself, and
    /// the bare <c>.</c> is precisely the case that would slip through.</para>
    /// </summary>
    public static string? Resolve(string repositories, string name)
    {
        if (LongPath.Configured(Path.Combine(repositories, name)) is not { } path)
        {
            return null;
        }

        return Path.GetDirectoryName(path) is { } container
            && container.Equals(repositories, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
    }
}
