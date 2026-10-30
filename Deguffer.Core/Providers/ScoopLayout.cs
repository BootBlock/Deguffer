namespace Deguffer.Core.Providers;

/// <summary>
/// Where one Scoop keeps what <c>scoop cache rm *</c> works on and what it must leave, worked out the
/// way that Scoop works it out when it starts.
/// </summary>
/// <param name="Command">The <c>scoop.cmd</c> shim the step starts, by full path.</param>
/// <param name="Root">
/// Scoop's root, <c>$scoopdir</c>: the folder holding <c>apps</c>, <c>buckets</c>, <c>persist</c>
/// and <c>shims</c>, none of which is cache.
/// </param>
/// <param name="Cache">The download cache, <c>$cachedir</c>, which the command empties.</param>
/// <param name="ConfigFiles">
/// Both settings files Scoop may read, the portable one beside the install and the per-user one,
/// whichever of them exists. Either can hold a GitHub token and a proxy password.
/// </param>
internal sealed record ScoopLayout(string Command, string Root, string Cache, IReadOnlyList<string> ConfigFiles);

/// <summary>Where one Scoop keeps its folders, or why Deguffer could not work it out exactly as Scoop does.</summary>
internal abstract record ScoopResolution
{
    private ScoopResolution()
    {
    }

    public sealed record Followed(ScoopLayout Layout) : ScoopResolution;

    /// <summary>Scoop's own reading could not be mirrored, for the reason given.</summary>
    public sealed record Unfollowed(string Why) : ScoopResolution;

    /// <summary>
    /// Followed to a root Deguffer will not examine as Scoop's, such as the profile or a drive root,
    /// for the reason <see cref="ConfiguredFolder.WhyNotOwned"/> gave.
    /// </summary>
    public sealed record RootDeclined(ScoopLayout Layout, string Why) : ScoopResolution;

    /// <summary>Followed to a cache Deguffer will not examine as Scoop's, for the reason given.</summary>
    public sealed record CacheDeclined(ScoopLayout Layout, string Why) : ScoopResolution;
}
