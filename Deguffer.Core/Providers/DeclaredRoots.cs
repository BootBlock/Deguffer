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
/// </summary>
internal static class DeclaredRoots
{
    private static readonly AsyncLocal<bool> Reading = new();

    /// <summary>
    /// Whether the code running now was called from a row's declaration while
    /// <see cref="ReadAsync"/> reads it. Such code must not wait for what it is part of.
    /// </summary>
    public static bool IsBeingRead => Reading.Value;

    /// <summary>The roots every one of <paramref name="rows"/> declares, in their order.</summary>
    /// <param name="ct">
    /// Checked between rows. A row's declaration cannot be stopped part-way.
    /// </param>
    public static Task<IReadOnlyList<ToolRoot>> ReadAsync(IReadOnlyList<ICleanupProvider> rows, CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync<IReadOnlyList<ToolRoot>>(
            () =>
            {
                // Set inside the work, so it flows into each declaration and ends with the work.
                Reading.Value = true;

                List<ToolRoot> roots = [];

                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    roots.AddRange(row.ToolRoots);
                }

                return roots;
            },
            ct);
}
