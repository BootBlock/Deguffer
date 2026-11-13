using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// How long each provider took to plan in the recent full planning passes (#443), so a later pass can
/// be ordered and weighed by what each row cost last time rather than by a guess.
///
/// <para><b>Only a full pass is kept.</b> A re-plan of the rows a clean changed measures emptied
/// folders, a scan of the locations a user chose plans beside fewer rows, and a cancelled pass ends
/// rows early. Each would read as a faster provider than a full pass finds. <see cref="RecordFullPass"/>
/// refuses any pass whose times do not cover every provider of the planner exactly once, so a caller
/// that hands it the wrong pass writes nothing rather than a false figure.</para>
///
/// <para><b>The median of the last few, for each provider and each elevation.</b> One slow pass, a
/// tool updating itself or a cold disk, then moves nothing, and a pass with administrator rights,
/// which asks questions one without them never does, is never averaged with one without.</para>
///
/// <para>Shared by every caller, as one instance, for the reason <see cref="Sizes.ProviderSizeHistory"/>
/// is. The file is read at the first question and held from then on.</para>
/// </summary>
/// <param name="time">The clock a kept pass is dated by. The system's by default.</param>
public sealed class PlanTimeHistory(PlanTimeStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How many full passes are kept for each provider at each elevation.</summary>
    public const int KeptPerElevation = 5;

    private readonly Lock _gate = new();

    private Dictionary<string, IReadOnlyList<PlanTimePoint>>? _kept;

    /// <summary>
    /// The median of the kept times of the provider <paramref name="providerId"/> for passes at
    /// <paramref name="elevated"/>, or null where none is kept.
    /// </summary>
    public TimeSpan? MedianOf(string providerId, bool elevated)
    {
        TimeSpan[] times;

        lock (_gate)
        {
            times =
            [
                .. (KeptLocked().GetValueOrDefault(providerId) ?? [])
                    .Where(point => point.Elevated == elevated)
                    .Select(point => point.Elapsed)
                    .Order(),
            ];
        }

        return times.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => times[count / 2],
            var count => (times[(count / 2) - 1] + times[count / 2]) / 2,
        };
    }

    /// <summary>
    /// Keep what a full pass that has just ended timed, dated now. Returns whether it was stored:
    /// false where <paramref name="times"/> is not one time for each of <paramref name="providers"/>,
    /// or where the file could not be written.
    /// </summary>
    /// <param name="providers">Every provider of the planner that ran the pass.</param>
    /// <param name="elevated">Whether the pass held administrator rights.</param>
    public bool RecordFullPass(
        IReadOnlyList<ICleanupProvider> providers,
        IReadOnlyList<ProviderPlanTime> times,
        bool elevated)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(times);

        if (!CoversEach(providers, times))
        {
            return false;
        }

        var takenUtc = _time.GetUtcNow().UtcDateTime;

        lock (_gate)
        {
            var kept = KeptLocked();

            foreach (var time in times)
            {
                var id = time.Provider.Id;
                kept[id] = With(kept.GetValueOrDefault(id) ?? [], new PlanTimePoint(takenUtc, time.Elapsed, elevated));
            }

            return store.Save(kept);
        }
    }

    /// <summary>Whether <paramref name="times"/> holds exactly one time for each of <paramref name="providers"/>, and no other.</summary>
    private static bool CoversEach(IReadOnlyList<ICleanupProvider> providers, IReadOnlyList<ProviderPlanTime> times)
    {
        if (providers.Count == 0 || times.Count != providers.Count)
        {
            return false;
        }

        var timed = new HashSet<ICleanupProvider>(times.Select(time => time.Provider), ReferenceEqualityComparer.Instance);

        return timed.Count == providers.Count && providers.All(timed.Contains);
    }

    /// <summary>
    /// <paramref name="points"/> with <paramref name="point"/> added, keeping the newest
    /// <see cref="KeptPerElevation"/> of each elevation, oldest first. A point no later than the newest
    /// is a clock that moved back, and it replaces what is newer rather than sitting out of order.
    /// </summary>
    private static IReadOnlyList<PlanTimePoint> With(IReadOnlyList<PlanTimePoint> points, PlanTimePoint point)
    {
        var earlier = points.Where(kept => kept.TakenUtc < point.TakenUtc).ToList();
        var sameElevation = earlier.Count(kept => kept.Elevated == point.Elevated);
        var dropping = Math.Max(0, sameElevation + 1 - KeptPerElevation);

        var result = new List<PlanTimePoint>(earlier.Count + 1);

        foreach (var kept in earlier)
        {
            if (kept.Elevated == point.Elevated && dropping > 0)
            {
                dropping--;
                continue;
            }

            result.Add(kept);
        }

        result.Add(point);

        return result;
    }

    private Dictionary<string, IReadOnlyList<PlanTimePoint>> KeptLocked() =>
        _kept ??= new Dictionary<string, IReadOnlyList<PlanTimePoint>>(store.Load(), StringComparer.Ordinal);
}
