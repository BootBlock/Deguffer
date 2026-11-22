using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where NuGet keeps its caches, and whether NuGet itself is what said so.</summary>
/// <param name="Paths">Every location, in the order NuGet listed them. Never empty.</param>
/// <param name="Answered">
/// False when <c>nuget locals all --list</c> failed or listed no rooted path, in which case
/// <paramref name="Paths"/> are the documented defaults, and the plan says so: see
/// <see cref="AskedLocation"/> for why the difference reaches the user.
/// </param>
internal sealed record NuGetLocals(IReadOnlyList<string> Paths, bool Answered)
{
    /// <summary>
    /// Asks NuGet where its caches are. Output lines look like
    /// <c>global-packages: C:\Users\me\.nuget\packages\</c>.
    /// </summary>
    /// <param name="defaults">The documented locations, the answer where NuGet gives none.</param>
    public static async Task<NuGetLocals> AskAsync(
        IProcessRunner runner,
        string dotnet,
        IReadOnlyList<string> defaults,
        CancellationToken ct)
    {
        var outcome = await runner.RunAsync(dotnet, "nuget locals all --list", ToolRun.Query, ct).ConfigureAwait(false);

        var parsed = new List<string>();
        if (outcome.Succeeded)
        {
            foreach (var line in outcome.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                if (Path.IsPathRooted(value))
                {
                    parsed.Add(value.TrimEnd('\\', '/'));
                }
            }
        }

        return parsed.Count > 0 ? new(parsed, Answered: true) : new(defaults, Answered: false);
    }

    /// <summary>
    /// The same list in the same order, which a record compares by reference. <see cref="ToolAnswerMemory"/>
    /// trusts an answer only once the next ask gives an equal one, so reference equality would never
    /// let NuGet's answer be trusted.
    /// </summary>
    public bool Equals(NuGetLocals? other) =>
        other is not null
        && Answered == other.Answered
        && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Answered);

        foreach (var path in Paths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
