using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// How many times to ask Windows to remove a scratch tree, and how long to wait in between.
///
/// <para>The two callers want opposite things. Disposing a <see cref="TempDirectory"/> is the only
/// chance that tree will get while the run can still use the time, so it waits out a handle
/// something else holds. <see cref="ScratchRoot"/>'s sweep runs before the first test and has every
/// later run behind it, so it takes one attempt and moves on rather than spending the backoff on
/// each of yesterday's trees in turn.</para>
///
/// <para>Two seconds of patience, because less was measurably not enough. A run straight after a
/// rebuild left fourteen trees behind with 200ms to spend and none with 2s, which fits a virus
/// scanner reading each newly written file once. A tree still refused after that waits for the
/// sweep, so the ceiling costs a doomed tree two seconds and buys back the other fourteen.</para>
/// </summary>
internal readonly record struct RemovalAttempts(int Count, TimeSpan Between)
{
    internal static readonly RemovalAttempts One = new(1, TimeSpan.Zero);

    internal static readonly RemovalAttempts WithBackoff = new(20, TimeSpan.FromMilliseconds(100));
}

/// <summary>
/// Removes one scratch tree, tolerating the handle a virus scanner or the search indexer keeps open
/// for a moment after a test stops writing to it, and the read-only bit a test set on something
/// inside it.
/// </summary>
internal static class ScratchTree
{
    /// <summary>
    /// Remove <paramref name="tree"/> and report whether it is gone.
    ///
    /// <para>Never throws for a refused delete. A scratch tree left behind must not turn a green run
    /// red, and neither caller has anyone to report to: one is a <c>Dispose</c>, and the other is a
    /// sweep whose whole purpose is to collect what an earlier refusal left.</para>
    /// </summary>
    internal static bool TryRemove(string tree, RemovalAttempts attempts)
    {
        // Extended form: tests deliberately build trees past MAX_PATH, and cleanup has to reach
        // them too (§6.3).
        var extended = LongPath.Extended(tree);
        var retriedAfterClearing = false;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(extended, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // DirectoryNotFoundException derives from IOException, so a tree another run's
                // sweep already took lands here and the existence check answers it.
                if (!Directory.Exists(extended))
                {
                    return true;
                }

                // Windows refuses a read-only entry on every attempt, so no amount of waiting
                // removes it, and the recursive delete never clears the bit itself. A tree holding
                // one used to be refused by its own Dispose and by every later sweep, for good. The
                // retry after clearing is taken at once and only once: the wait is for a handle,
                // and the attempt count is what bounds a tree that is refused for some other reason.
                if (ClearReadOnly(extended) && !retriedAfterClearing)
                {
                    retriedAfterClearing = true;
                    continue;
                }

                if (attempt >= attempts.Count)
                {
                    return false;
                }

                Thread.Sleep(attempts.Between);
            }
        }
    }

    /// <summary>
    /// Clear the read-only bit on every entry the delete left in <paramref name="extendedTree"/>, the
    /// tree's own directory included, and say whether any had it.
    ///
    /// <para>A link is cleared as an entry and never descended into. Its own bit refuses the delete as
    /// a directory's does, but what it points at is outside the tree, and clearing the far side's
    /// attributes would change a directory no test handed over. Windows sets a link's attributes on
    /// the link itself, so clearing one does not reach through it either.</para>
    ///
    /// <para>An entry that cannot be listed or changed is passed over: the delete that follows
    /// reports the tree as refused, which is the answer a caller can act on.</para>
    /// </summary>
    private static bool ClearReadOnly(string extendedTree)
    {
        var root = new DirectoryInfo(extendedTree);
        var cleared = TryClearReadOnly(root, root.Attributes);
        var pending = new Stack<DirectoryInfo>([root]);

        while (pending.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", DirectoryListing.EveryEntry))
                {
                    // Read once, from the listing: setting them invalidates what the entry holds.
                    var attributes = entry.Attributes;

                    cleared |= TryClearReadOnly(entry, attributes);

                    if (entry is DirectoryInfo child && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that will not be listed keeps whatever is in it read-only.
            }
        }

        return cleared;
    }

    /// <summary>Clear <paramref name="entry"/>'s read-only bit and no other, and say whether it was set.</summary>
    private static bool TryClearReadOnly(FileSystemInfo entry, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return false;
        }

        try
        {
            entry.Attributes = attributes & ~FileAttributes.ReadOnly;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
