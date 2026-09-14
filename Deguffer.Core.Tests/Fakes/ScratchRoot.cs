using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The one directory every <see cref="TempDirectory"/> is made under, and the sweep that clears what
/// earlier runs could not remove.
///
/// <para>A refused delete is forgiven on purpose, because a tree a scanner still holds must not turn
/// a green run red. Nothing collected the forgiven ones until this existed, so each one was
/// permanent and the root grew for as long as the machine was used. The sweep is the other half of
/// that bargain: yesterday's leak goes today.</para>
/// </summary>
internal static class ScratchRoot
{
    /// <summary>
    /// How old a tree has to be before the sweep will touch it.
    ///
    /// <para>The margin is what keeps the sweep off a tree another process is still using: a
    /// concurrent run would have to have been going for an hour for its trees to qualify, and this
    /// suite finishes in minutes. Age comes from the creation time, which is stamped once, rather
    /// than the last-write time, which stops moving while the test that owns the tree runs on.</para>
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>Where scratch trees live, under TEMP.</summary>
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deguffer-tests");

    private static readonly Lazy<bool> Swept = new(() =>
    {
        SweepStale(Path, StaleAfter);
        return true;
    });

    /// <summary>
    /// Whether this process has swept yet.
    ///
    /// <para>The suite asserts on it, because nothing else can observe that making the first scratch
    /// tree of a run is what triggers the sweep.</para>
    /// </summary>
    internal static bool HasSwept => Swept.IsValueCreated;

    /// <summary>Clear earlier runs' leavings, once per test process.</summary>
    internal static void SweepOnce() => _ = Swept.Value;

    /// <summary>
    /// Remove every recognised child of <paramref name="root"/> created more than
    /// <paramref name="olderThan"/> ago, and nothing else.
    ///
    /// <para>"Recognised" is §5.2's rule turned on the suite's own scratch. The root itself is never
    /// a target, and a child whose name is not one <see cref="TempDirectory"/> writes is left alone,
    /// because TEMP is a place people put things. A child that will not go is left for the next run
    /// rather than retried, so the sweep costs the run it delays almost nothing.</para>
    /// </summary>
    internal static void SweepStale(string root, TimeSpan olderThan)
    {
        var extended = LongPath.Extended(root);

        if (!Directory.Exists(extended))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - olderThan;

        // Listed before the first delete: removing entries from a directory while enumerating it can
        // make the enumeration skip the ones after it, and a skipped tree would wait another run for
        // no reason. The list is the root's stale children, never a tree.
        var stale = Directory.EnumerateDirectories(extended)
            .Where(child => IsScratchTree(System.IO.Path.GetFileName(child)))
            .Where(child => Directory.GetCreationTimeUtc(child) < cutoff)
            .ToList();

        foreach (var tree in stale)
        {
            ScratchTree.TryRemove(tree, RemovalAttempts.One);
        }
    }

    /// <summary>Whether <paramref name="name"/> has the shape <see cref="TempDirectory"/> gives a tree.</summary>
    internal static bool IsScratchTree(string name) => Guid.TryParseExact(name, "N", out _);
}
