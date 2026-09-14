using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The one directory every <see cref="TempDirectory"/> is made under, and the sweep that clears what
/// earlier runs could not remove.
///
/// <para>A refused delete is forgiven on purpose, because a tree a scanner still holds must not turn
/// a green run red. Nothing collected the forgiven ones until this existed, so each one was
/// permanent and the root grew for as long as the machine was used. The sweep is the other half of
/// that bargain: a tree the hold has since let go of is taken on the next run.</para>
///
/// <para>What it does not collect is a tree the account may no longer delete.
/// <see cref="DeniedDirectory"/> and <see cref="UndeletableFile"/> put such rules on, and both take
/// them off again in their own <c>Dispose</c> and on a part-way failure, so this only arises when
/// the test host is killed outright. A retry count is the wrong instrument for a DACL, and lifting
/// someone's access rules in TEMP is a larger power than a leak of this size justifies.</para>
/// </summary>
internal static class ScratchRoot
{
    /// <summary>Where scratch trees live, under TEMP.</summary>
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deguffer-tests");

    /// <summary>
    /// What Windows answers for an entry that is not there. .NET returns it rather than throwing,
    /// and it is older than any cutoff, so a sweep that compared it straight would read "there is
    /// nothing here to date" as "certainly stale" and delete on it.
    /// </summary>
    private static readonly DateTime NotFound = DateTime.FromFileTimeUtc(0);

    private static readonly Lazy<bool> Swept = new(() =>
    {
        SweepStale(Path, Scratch.StaleAfter);
        return true;
    });

    /// <summary>
    /// Whether this process has swept yet.
    ///
    /// <para>The suite asserts on it, because the sweep runs before any test can watch it and
    /// nothing it leaves behind distinguishes "swept and found nothing" from "never ran".</para>
    /// </summary>
    internal static bool HasSwept => Swept.IsValueCreated;

    /// <summary>Clear earlier runs' leavings, once per test process.</summary>
    internal static void SweepOnce() => _ = Swept.Value;

    /// <summary>
    /// Remove every recognised child of <paramref name="root"/> created more than
    /// <paramref name="olderThan"/> ago, and nothing else.
    ///
    /// <para>"Recognised" is §5.2's rule turned on the suite's own scratch. The root itself is never
    /// a target, and a child whose name is not one <see cref="Scratch.NewIdentifier"/> writes is
    /// left alone, because TEMP is a place people put things. A child that will not go is left for the next run
    /// rather than retried, so the sweep costs the run it delays almost nothing.</para>
    /// </summary>
    internal static void SweepStale(string root, TimeSpan olderThan)
    {
        foreach (var tree in Stale(root, DateTime.UtcNow - olderThan))
        {
            ScratchTree.TryRemove(tree, RemovalAttempts.One);
        }
    }

    /// <summary>
    /// Whether <paramref name="child"/> was created before <paramref name="cutoff"/>.
    ///
    /// <para>Age comes from the creation time, which is stamped once, rather than the last-write
    /// time, which stops moving while the test that owns the tree runs on.</para>
    ///
    /// <para>A time we could not read answers no. This is the predicate guarding a recursive delete,
    /// and the only safe reading of "I cannot tell" on such a predicate is the one that stops it.
    /// The entry going between the listing and this call is the ordinary way to reach it.</para>
    ///
    /// <para>Windows says so two ways, and only one of them is a value. An entry that is not there
    /// comes back as <see cref="NotFound"/>, while one the account may not read the attributes of
    /// throws <see cref="UnauthorizedAccessException"/>. The catch belongs here rather than around
    /// the listing, so that one child nobody can date leaves the rest of the sweep to get on with
    /// it instead of cancelling the lot.</para>
    /// </summary>
    internal static bool IsStale(string child, DateTime cutoff)
    {
        try
        {
            var created = Directory.GetCreationTimeUtc(child);

            return created != NotFound && created < cutoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The stale scratch trees under <paramref name="root"/>, all of them listed before the first
    /// one is deleted: removing entries from a directory while enumerating it can make the
    /// enumeration skip the ones after it, and a skipped tree would wait another run for no reason.
    /// The list holds the root's children, never a tree's contents.
    /// </summary>
    private static IReadOnlyList<string> Stale(string root, DateTime cutoff)
    {
        try
        {
            return Directory.EnumerateDirectories(LongPath.Extended(root))
                .Where(child => Scratch.IsIdentifier(System.IO.Path.GetFileName(child)))
                .Where(child => IsStale(child, cutoff))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Including the root not being there at all, which is every machine's first run.
            // A sweep that cannot read the root has nothing to say, and must not be the thing that
            // fails the run: Lazy keeps a faulting factory's exception for the life of the process,
            // so one throw here would be re-thrown by every TempDirectory the run went on to make.
            return [];
        }
    }
}
