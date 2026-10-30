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

/// <summary>
/// A <see cref="ScoopLayout"/>, or the sentence saying why Deguffer could not work it out exactly as
/// Scoop does. Exactly one of the two is set.
/// </summary>
internal sealed record ScoopResolution(ScoopLayout? Layout, string? WhyUnfollowed)
{
    public static ScoopResolution Of(ScoopLayout layout) => new(layout, null);

    public static ScoopResolution Unfollowed(string why) => new(null, why);
}
