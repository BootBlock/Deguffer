using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Poetry says its two directories are on this machine.
/// </summary>
/// <param name="CacheRoot">
/// The cache directory, which is the folder Deguffer reaches into. Where Poetry cannot answer, this
/// is the documented default, and the provider still checks it exists before acting.
/// </param>
/// <param name="Environments">
/// Where Poetry keeps its virtual environments, and the reason this record has two fields rather
/// than one: <c>virtualenvs.path</c> defaults to a child of <paramref name="CacheRoot"/> and can be
/// pointed anywhere, so the two are separate answers that happen to overlap by default. A provider
/// that derived this from the cache root would be asserting the default rather than describing the
/// machine. Where Poetry cannot answer, this is that default.
/// </param>
internal sealed record PoetryLocations(AskedLocation CacheRoot, AskedLocation Environments)
{
    /// <summary>
    /// What the plan says about Poetry's two folders, each as Poetry's report or as a default.
    ///
    /// <para>A guessed cache directory is worded for Poetry's row rather than by
    /// <see cref="AskedLocation.Describe"/>, because half of that plan is Deguffer's own deletion:
    /// the archives it removes are in the folder named here, while Poetry's clear acts on wherever
    /// Poetry really keeps its cache.</para>
    /// </summary>
    public string Describe()
    {
        var (cacheRoot, environments) = (LongPath.Display(CacheRoot.Path), LongPath.Display(Environments.Path));

        var cache = CacheRoot.Answered
            ? $"Poetry reports its cache directory as {cacheRoot}."
            : $"Poetry did not say where its cache directory is, so {cacheRoot} is the documented default "
              + "rather than this machine's setting. What Deguffer removes itself is in that folder, and "
              + "Poetry's own cache clear acts on wherever Poetry really keeps its cache.";

        var kept = Environments.Answered
            ? $"Poetry reports its virtual environments as {environments}."
            : $"Poetry did not say where it keeps its virtual environments, so Deguffer takes them to be in "
              + $"{environments}, where Poetry creates them by default, and leaves that folder alone.";

        return $"{cache} {kept}";
    }
}
