using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every row's <see cref="ICleanupProvider.ToolRoots"/>, read on a thread of the walk's own rather
/// than the caller's: the one way async code reads them.
///
/// <para><b>A declaration may walk.</b> The Chromium rows build theirs from a walk of both
/// application-data roots, the first time they are asked after an invalidation, and a walk holds the
/// thread that drives it for its whole length. Read from a pool thread, that is a pool thread held for
/// all of it, and every caller waiting on the same answer holds another. Read here, the caller awaits
/// it holding none.</para>
///
/// <para><b>Several rows are read at once.</b> Many rows build theirs from a walk or a survey, and
/// read one after another those ran in series, before Explore's first verdict and
/// before the first plan of a pass could ask what other rows offer. Each row's memo is shared by
/// every thread that asks it (<see cref="PassMemo{T}"/>), so two rows reading one discovery at once
/// walk it once.</para>
/// </summary>
internal static class DeclaredRoots
{
    private static readonly AsyncLocal<bool> Reading = new();

    /// <summary>
    /// Whether the code running now was called from a row's declaration while
    /// <see cref="ReadAsync(ICleanupProvider, CancellationToken)"/> reads it. Such code must not wait
    /// for what it is part of.
    /// </summary>
    public static bool IsBeingRead => Reading.Value;

    /// <summary>The roots <paramref name="row"/> declares.</summary>
    /// <param name="ct">
    /// Checked before the row is read. A row's declaration is a property, and cannot be stopped
    /// part-way. From a <see cref="Parallel.ForAsync{T}(T, T, ParallelOptions, Func{T, CancellationToken, ValueTask})"/>
    /// body, pass the caller's token rather than the body's: the body's is cancelled from the
    /// caller's by a callback, which can run after a row has ended and its slot has gone to the next.
    /// </param>
    public static Task<IReadOnlyList<ToolRoot>> ReadAsync(ICleanupProvider row, CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync(
            () =>
            {
                // Set inside the work, so it flows into the declaration and ends with the work.
                Reading.Value = true;
                return row.ToolRoots;
            },
            ct);

    /// <summary>
    /// The roots every one of <paramref name="rows"/> declares, in their order, read as many at once
    /// as a pass plans (G4): each read holds a thread of its own for as long as its row walks.
    /// </summary>
    /// <param name="ct">
    /// Checked before each row is read. A row already being read is waited for.
    /// </param>
    public static async Task<IReadOnlyList<ToolRoot>> ReadAsync(IReadOnlyList<ICleanupProvider> rows, CancellationToken ct)
    {
        var declared = new IReadOnlyList<ToolRoot>[rows.Count];

        await Parallel.ForAsync(
            0,
            rows.Count,
            new ParallelOptions { MaxDegreeOfParallelism = CleanupPlanner.ProvidersAtOnce, CancellationToken = ct },
            async (index, _) => declared[index] = await ReadAsync(rows[index], ct).ConfigureAwait(false))
            .ConfigureAwait(false);

        return [.. declared.SelectMany(roots => roots)];
    }
}
