using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What one step's <see cref="DeleteStep.UseCheck"/> said immediately before the run reached it, and
/// what the run does about that.
/// </summary>
/// <param name="Step">
/// The step as it may run now: itself where nothing is in use, or a folder cleared in place with the
/// entries now in use added to its spared set. Null where it must not run at all.
/// </param>
/// <param name="Withheld">Why it must not run, as a clause. Null wherever <paramref name="Step"/> is not.</param>
/// <param name="Survivors">
/// What the run now owes §5.6: every place held back here, asserted to be standing afterwards as a
/// step the user declined is. The check is what kept each one, so the check is what the negative
/// has to prove.
/// </param>
internal sealed record UseRecheck(DeleteStep? Step, string? Withheld, IReadOnlyList<ProtectedPath> Survivors)
{
    /// <summary>Ask <paramref name="step"/>'s check, or pass it through where it carries none.</summary>
    public static UseRecheck Of(DeleteStep step, CancellationToken ct)
    {
        if (step.UseCheck is not { } check || check.Ask(step, ct) is not { Count: > 0 } inUse)
        {
            return new UseRecheck(step, Withheld: null, Survivors: []);
        }

        // Only a folder cleared in place can leave an entry standing and still do its job. Anything
        // else something is using inside is held back whole: a build directory with a compiler
        // running from it, or a session's folder with the session writing to it, is not a set of
        // independent files to remove around the one in use.
        if (step is ClearDirectoryStep clear && SpareableEntries(clear, inUse) is { } spared)
        {
            var added = spared.Where(entry => !clear.Spared.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)).ToList();

            return new UseRecheck(
                clear with { Spared = [.. clear.Spared, .. added.Select(entry => entry.Path)] },
                Withheld: null,
                [.. added.Select(entry => Survivor(entry.Path, $"Left alone because {entry.Reason}."))]);
        }

        var reason = string.Join("; ", inUse.Select(entry => entry.Reason).Distinct(StringComparer.Ordinal));

        return new UseRecheck(
            Step: null,
            reason,
            [.. step.Destroys.Select(path => Survivor(path, $"Left alone because {reason}."))]);
    }

    /// <summary>
    /// Each place in use as the entry directly inside <paramref name="clear"/>'s folder that holds it,
    /// or null where one of them cannot be spared that way.
    ///
    /// <para>Widened to the entry because that is the only thing the removal can spare: it matches a
    /// spared entry by its own path, so a place deeper down would have the walk go into the entry and
    /// remove everything around it. The folder itself, or a place outside it, cannot be spared at all,
    /// and the whole step is held back instead, which is the direction a check that answered about
    /// the wrong place must fail in.</para>
    /// </summary>
    private static IReadOnlyList<InUseNow>? SpareableEntries(ClearDirectoryStep clear, IReadOnlyList<InUseNow> inUse)
    {
        var root = Path.TrimEndingDirectorySeparator(LongPath.Display(clear.Path));
        var entries = new Dictionary<string, InUseNow>(StringComparer.OrdinalIgnoreCase);

        foreach (var place in inUse)
        {
            var path = Path.TrimEndingDirectorySeparator(LongPath.Display(place.Path));

            if (path.Length <= root.Length || !LongPath.Contains(root, path))
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, path);
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var entry = Path.Combine(root, separator < 0 ? relative : relative[..separator]);

            entries.TryAdd(entry, place with { Path = entry });
        }

        return [.. entries.Values];
    }

    /// <summary>
    /// A place the check found in use. It was there when the plan was made, because the plan measured
    /// it as part of a step, and nothing is claimed about what it holds: something is using it, and
    /// that something may empty it.
    /// </summary>
    private static ProtectedPath Survivor(string path, string reason) =>
        new(path, reason, PresenceBefore: PathPresence.Present);
}
