using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>scoop cache rm *</c> would still empty the folder the plan examined, and find only what
/// Scoop wrote there, asked again immediately before it runs.
///
/// <para><b>Why the plan's answer is not enough.</b> The command lists the cache when it runs and
/// removes every entry it finds (<c>libexec/scoop-cache.ps1</c> at
/// <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>), and the command names no item, only the folder.
/// A file saved into the cache between the preview and the clean would go with the downloads, and
/// §5.6 could not notice, because a file the plan never listed is not one it protects. Scoop also
/// reads its settings afresh when it starts, so a <c>cache_path</c> edited in between sends it to a
/// folder nobody examined.</para>
/// </summary>
/// <param name="Environment">What Scoop's location is worked out from again.</param>
/// <param name="Cache">The folder the plan examined.</param>
internal sealed record ScoopCacheContents(IUserEnvironment Environment, string Cache) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (ScoopLocations.Resolve(Environment, step.FileName) is not ScoopResolution.Followed { Layout: var layout }
            || !LongPath.Display(layout.Cache).Equals(LongPath.Display(Cache), StringComparison.OrdinalIgnoreCase))
        {
            return $"Scoop no longer reads its download cache as {Cache}, the folder the preview examined";
        }

        if (LongPath.IsReparsePoint(Cache))
        {
            return $"Scoop's download cache at {Cache} is now a link to somewhere else";
        }

        return ScoopCacheEntries.Of(Cache, ct) switch
        {
            null => $"Windows would not list Scoop's download cache at {Cache} to the end, so Deguffer cannot "
                + "tell what Scoop's command would remove",
            { Unrecognised.Count: > 0 } entries => $"Scoop's download cache at {Cache} now holds "
                + $"{entries.DescribeUnrecognised()}, and Scoop's command removes everything it finds there",
            _ => null,
        };
    }
}
