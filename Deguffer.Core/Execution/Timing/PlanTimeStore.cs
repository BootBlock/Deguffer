using System.Collections.ObjectModel;
using System.Text.Json;
using Deguffer.Core.Execution.Sizes;
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
/// <para><b>A file that cannot be read is ignored whole</b>, as <see cref="ProviderSizeStore"/> ignores
/// one: a torn write, a hand edit and a format this build does not know all read as no history.</para>
/// </summary>
public sealed class PlanTimeStore
{
    /// <summary>The format this build writes and reads. A file of any other is not read.</summary>
    private const int Format = 1;

    /// <summary>The longest time a <see cref="TimeSpan"/> holds, so a hand-edited figure past it is refused rather than thrown.</summary>
    private const long LongestMicroseconds = long.MaxValue / TimeSpan.TicksPerMicrosecond;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>> None =
        ReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>>.Empty;

    private readonly string _file;

    public PlanTimeStore(IUserEnvironment environment) =>
        _file = OwnDataFolder.FileFor(environment, "planning-times.json");

    /// <summary>
    /// Each provider's times, oldest first, or none where the file is missing, cannot be read, or is
    /// not wholly one this store would write.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PlanTimePoint>> Load()
    {
        Stored? stored;

        try
        {
            stored = JsonSerializer.Deserialize(StoreFile.Read(_file), StrictStoreJson.Default.PlanningTimes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing before the first full pass, refused, torn, or not this format at all.
            return None;
        }

        // The annotations are not enforced on what a dictionary or an array holds, so a null there is
        // refused here, as the rest of a file this store would not write is, and so is a figure no
        // duration can hold.
        if (stored is not { Format: Format }
            || stored.Providers.Values.Any(points => points is null || points.Any(point => point is not { Microseconds: >= 0 and <= LongestMicroseconds })))
        {
            return None;
        }

        var providers = stored.Providers.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(point => point.ToPoint()).ToArray(),
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

        try
        {
            var stored = new Stored(
                Format,
                providers.ToDictionary(entry => entry.Key, entry => entry.Value.Select(StoredTime.Of).ToArray(), StringComparer.Ordinal));

            StoreFile.Replace(_file, JsonSerializer.Serialize(stored, StrictStoreJson.Default.PlanningTimes));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether every entry is one this store writes: a provider's id, and its times in order, each a UTC
    /// instant with a duration no shorter than zero.
    /// </summary>
    private static bool Usable(IReadOnlyDictionary<string, PlanTimePoint[]> providers) =>
        providers.All(entry => ProviderSizeStore.IsProviderId(entry.Key) && Ordered(entry.Value));

    private static bool Ordered(PlanTimePoint[] points)
    {
        for (var at = 0; at < points.Length; at++)
        {
            if (points[at].Elapsed < TimeSpan.Zero
                || points[at].TakenUtc.Kind is not DateTimeKind.Utc
                || (at > 0 && points[at].TakenUtc <= points[at - 1].TakenUtc))
            {
                return false;
            }
        }

        return points.Length > 0;
    }

    /// <summary>The file as it is written.</summary>
    internal sealed record Stored(int Format, Dictionary<string, StoredTime[]> Providers);

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
