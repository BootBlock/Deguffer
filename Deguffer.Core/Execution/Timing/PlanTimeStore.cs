using System.Collections.ObjectModel;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// Reads and writes each provider's <see cref="PlanTimePoint"/>s as JSON in
/// <c>%LOCALAPPDATA%\Deguffer\planning-times.json</c> (#443).
///
/// <para><b>Apart from <see cref="Scanning.ScanEstimateCache"/>.</b> That file is keyed by path, read
/// for first paint, and written by every measurement, the measurement after a clean included. A time
/// is keyed by provider, and only a full pass is the population it describes. Kept in one file, a
/// format change to either would cost the other its history, and a re-measure of an emptied folder
/// would sit beside the passes it is no part of.</para>
///
/// <para><b>A file that cannot be read is ignored whole</b>, and nothing but a provider's id names an
/// entry, as for every <see cref="ProviderPointFile{TStored}"/>.</para>
/// </summary>
public sealed class PlanTimeStore
{
    /// <summary>The format this build writes and reads. A file of any other is not read.</summary>
    private const int Format = 1;

    /// <summary>The longest time a <see cref="TimeSpan"/> holds, so a hand-edited figure past it is refused rather than thrown.</summary>
    private const long LongestMicroseconds = long.MaxValue / TimeSpan.TicksPerMicrosecond;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>> None =
        ReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>>.Empty;

    private readonly ProviderPointFile<StoredTime> _file;

    public PlanTimeStore(IUserEnvironment environment) =>
        _file = new(OwnDataFolder.FileFor(environment, "planning-times.json"), Format, StrictStoreJson.Default.PlanningTimes);

    /// <summary>
    /// Each provider's times, oldest first, or none where the file is missing, cannot be read, or is
    /// not wholly one this store would write.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>> Load()
    {
        // A figure no duration can hold is refused before it is made into one, which would throw.
        if (_file.Read() is not { } stored
            || stored.Values.Any(times => times.Any(time => time.Microseconds is < 0 or > LongestMicroseconds)))
        {
            return None;
        }

        var providers = stored.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(time => time.ToPoint()).ToArray(),
            StringComparer.Ordinal);

        return Usable(providers)
            ? providers.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<PlanTimePoint>)entry.Value, StringComparer.Ordinal)
            : None;
    }

    /// <summary>Store <paramref name="history"/> in place of what is stored. Returns whether it was written.</summary>
    public bool Save(IReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var providers = history.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);

        // Checked on the way out as on the way in, so this store never writes a file it would not read.
        if (!Usable(providers))
        {
            throw new ArgumentException("Every entry must be a provider's id with times oldest first.", nameof(history));
        }

        return _file.Write(providers.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(StoredTime.Of).ToArray(),
            StringComparer.Ordinal));
    }

    /// <summary>Whether every time is no shorter than zero, in order of when it was kept.</summary>
    private static bool Usable(IReadOnlyDictionary<string, PlanTimePoint[]> providers) =>
        ProviderPointFile.AreUsable(providers, point => point.TakenUtc, point => point.Elapsed >= TimeSpan.Zero);

    /// <summary>
    /// One time as it is written: every member a constructor parameter, so a time missing any of them
    /// is refused rather than read as a zero. In whole microseconds, which no plan is short enough for
    /// the difference to matter to and which a reader of the file can follow.
    /// </summary>
    internal sealed record StoredTime(DateTime TakenUtc, long Microseconds, bool Elevated)
    {
        public static StoredTime Of(PlanTimePoint point) =>
            new(point.TakenUtc, point.Elapsed.Ticks / TimeSpan.TicksPerMicrosecond, point.Elevated);

        public PlanTimePoint ToPoint() => new(TakenUtc, TimeSpan.FromMicroseconds(Microseconds), Elevated);
    }
}
