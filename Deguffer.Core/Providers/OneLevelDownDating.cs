using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The age of each deletion target that has none of its own and is dated by its newest child instead
/// (<see cref="DeletionTarget.DatedOneLevelDown"/>), read once for all of them. See
/// <see cref="CleanupProviderBase.DatedAsync"/>.
/// </summary>
internal static class OneLevelDownDating
{
    /// <summary>
    /// <paramref name="targets"/>, in their order, each one dated one level down given its age from
    /// <paramref name="date"/>, which reads every such directory at once.
    /// </summary>
    public static Task<IReadOnlyList<DeletionTarget>> DateAsync(
        IReadOnlyList<DeletionTarget> targets,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<DateTime?>>> date,
        CancellationToken ct)
    {
        ThrowIfMisdated(targets);

        return ReadAgesAsync(targets, date, ct);
    }

    /// <summary>
    /// Before anything is read, so a target that cannot be dated one level down fails its plan rather
    /// than reading null.
    /// </summary>
    public static void ThrowIfMisdated(IReadOnlyList<DeletionTarget> targets)
    {
        if (targets.FirstOrDefault(t => t.DatedOneLevelDown && (t.LastWritten is not null || t.Kind is not (TargetKind.Directory or TargetKind.DirectoryContents))) is { Path: { } misdated })
        {
            throw new ArgumentException($"Only a directory with no age of its own can be dated one level down: {misdated}.", nameof(targets));
        }
    }

    private static async Task<IReadOnlyList<DeletionTarget>> ReadAgesAsync(
        IReadOnlyList<DeletionTarget> targets,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<DateTime?>>> date,
        CancellationToken ct)
    {
        int[] undated = [.. Enumerable.Range(0, targets.Count).Where(index => targets[index].DatedOneLevelDown)];

        if (undated.Length == 0)
        {
            return targets;
        }

        var ages = await date([.. undated.Select(index => targets[index].Path)], ct).ConfigureAwait(false);
        var dated = targets.ToArray();

        for (var each = 0; each < undated.Length; each++)
        {
            dated[undated[each]] = dated[undated[each]] with { LastWritten = ages[each], DatedOneLevelDown = false };
        }

        return dated;
    }
}
