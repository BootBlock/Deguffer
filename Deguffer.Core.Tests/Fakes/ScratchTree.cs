using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

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
/// for a moment after a test stops writing to it.
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

                if (attempt >= attempts.Count)
                {
                    return false;
                }

                Thread.Sleep(attempts.Between);
            }
        }
    }
}
