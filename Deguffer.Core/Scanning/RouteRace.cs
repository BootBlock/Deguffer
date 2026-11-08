namespace Deguffer.Core.Scanning;

/// <summary>Which route answered a question the two routes raced for.</summary>
internal enum RaceOutcome
{
    /// <summary>The table answered, and the walk was stopped.</summary>
    TableAnswered,

    /// <summary>The walk answered while the table was still being read.</summary>
    WalkAnsweredFirst,

    /// <summary>The table could not answer, so the walk's answer is the only one.</summary>
    TableDeclined,
}

/// <summary>An answer, and which route gave it.</summary>
internal readonly record struct Raced<T>(T Answer, RaceOutcome Outcome);

/// <summary>
/// §5.5's two routes run at once, the first answer taken and the other route stopped. What
/// <see cref="Configuration.ScanRoute.Auto"/> does while a volume's table is being read.
///
/// <para>One place for every question that races, so that what counts as an answer, and that the
/// loser is stopped and waited for before the winner is returned, is one rule rather than one per
/// caller. Nothing about a route is chosen here: <see cref="DirectoryScanner"/> and
/// <see cref="Exploring.ExploreScanner"/> still decide whether a question races at all.</para>
/// </summary>
internal static class RouteRace
{
    /// <summary>
    /// The first of <paramref name="table"/> and <paramref name="walk"/> to answer.
    ///
    /// <para>The table answers with anything but null. The walk answers when
    /// <paramref name="answers"/> says it does: a walk that could not reach its path has said nothing
    /// the table cannot say better, so the table is waited for, and the walk's result is taken only
    /// if the table declines too.</para>
    ///
    /// <para>The loser is cancelled and waited for before this returns, so no walk is still reading
    /// the disk, or reporting progress, once its question has been answered. Each route is given a
    /// token cancelled for that, and a route must stop at it without treating it as a failure.</para>
    /// </summary>
    /// <param name="tableAlone">
    /// Called where the walk finishes first without an answer, before the table is waited for: the
    /// table is then the only route left answering, and has stopped being the speculative side of the
    /// race (<see cref="Mft.BuildPriorities"/>).
    /// </param>
    public static async Task<Raced<T>> FirstAsync<T>(
        Func<CancellationToken, Task<T?>> table,
        Func<CancellationToken, Task<T>> walk,
        Func<T, bool> answers,
        Action tableAlone,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(tableAlone);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var reading = table(stop.Token);
        var walking = walk(stop.Token);

        try
        {
            if (await Task.WhenAny((Task)reading, walking).ConfigureAwait(false) == walking)
            {
                if (answers(await walking.ConfigureAwait(false)))
                {
                    return new Raced<T>(walking.Result, RaceOutcome.WalkAnsweredFirst);
                }

                tableAlone();
            }

            if (await reading.ConfigureAwait(false) is { } fromTable)
            {
                return new Raced<T>(fromTable, RaceOutcome.TableAnswered);
            }

            return new Raced<T>(await walking.ConfigureAwait(false), RaceOutcome.TableDeclined);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);

            // Both waited for before either's failure leaves: a route that failed must not leave the
            // other still reading the disk, or reporting, behind a race that has ended.
            await Task.WhenAll(SettleAsync(reading), SettleAsync(walking)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait for a route once the race is over. Both have been told to stop by then, so a
    /// cancellation is what stopping looks like and is swallowed, and anything else a route threw is
    /// not. An answer or a failure that decided the race has already left through the body above.
    /// </summary>
    private static async Task SettleAsync(Task route)
    {
        try
        {
            await route.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
