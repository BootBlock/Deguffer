using System.Collections.ObjectModel;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution.Sizes;

/// <summary>
/// Reads and writes each provider's <see cref="SizePoint"/>s as JSON in
/// <c>%LOCALAPPDATA%\Deguffer\provider-sizes.json</c> (#358).
///
/// <para><b>A file that cannot be read is ignored whole, never compared.</b> A torn write, a hand edit
/// and a format this build does not know all arrive as no history, which costs each row one change.
/// Reading part of a damaged one could report a cache as emptied, or as gigabytes of growth.</para>
///
/// <para><b>Nothing but a provider's id names an entry, and an id is checked as one</b>
/// (<see cref="ProviderPointFile.IsProviderId"/>), on the way in and on the way out. That is what keeps
/// a path, and with it the user's layout, out of the file.</para>
/// </summary>
public sealed class ProviderSizeStore
{
    /// <summary>
    /// How many figures are kept per provider. With one a day (<see cref="ProviderSizeHistory"/>),
    /// that is two months of daily scans, and longer for a user who scans less often.
    /// </summary>
    public const int KeptPerProvider = 60;

    /// <summary>The format this build writes and reads. A file of any other is not read.</summary>
    private const int Format = 1;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> None =
        ReadOnlyDictionary<string, IReadOnlyList<SizePoint>>.Empty;

    private readonly ProviderPointFile<StoredPoint> _file;

    public ProviderSizeStore(IUserEnvironment environment) =>
        _file = new(OwnDataFolder.FileFor(environment, "provider-sizes.json"), Format, StrictStoreJson.Default.ProviderSizes);

    /// <summary>
    /// Each provider's figures, oldest first, or none where the file is missing, cannot be read, or is
    /// not wholly one this store would write.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> Load()
    {
        if (_file.Read() is not { } stored)
        {
            return None;
        }

        var providers = stored.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(point => point.ToPoint()).ToArray(),
            StringComparer.Ordinal);

        return Usable(providers)
            ? providers.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<SizePoint>)entry.Value, StringComparer.Ordinal)
            : None;
    }

    /// <summary>Store <paramref name="history"/> in place of what is stored. Returns whether it was written.</summary>
    public bool Save(IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var providers = history.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);

        // Checked on the way out as on the way in, so this store never writes a file it would not read.
        if (!Usable(providers))
        {
            throw new ArgumentException("Every entry must be a provider's id with figures oldest first.", nameof(history));
        }

        return _file.Write(providers.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(StoredPoint.Of).ToArray(),
            StringComparer.Ordinal));
    }

    /// <summary>Remove the stored history. Returns whether none is left.</summary>
    public bool Remove() => _file.Remove();

    /// <summary>Whether every figure is a size and a count no smaller than zero, in order of time.</summary>
    private static bool Usable(IReadOnlyDictionary<string, SizePoint[]> providers) =>
        ProviderPointFile.AreUsable(providers, point => point.TakenUtc, point => point is { Bytes: >= 0, Entries: >= 0 });

    /// <summary>
    /// One figure as it is written: a class with every member a constructor parameter, so a figure
    /// missing any of them is refused rather than read as a zero.
    /// </summary>
    internal sealed record StoredPoint(
        DateTime TakenUtc, long Bytes, long Entries, bool Approximate, bool Ceiling, bool Elevated)
    {
        public static StoredPoint Of(SizePoint point) =>
            new(point.TakenUtc, point.Bytes, point.Entries, point.Approximate, point.Ceiling, point.Elevated);

        public SizePoint ToPoint() => new(TakenUtc, Bytes, Entries, Approximate, Ceiling, Elevated);
    }
}
