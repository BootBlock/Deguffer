using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <para><b>Nothing but a provider's id names an entry, and an id is checked as one.</b> Every id is a
/// fixed lower-case word (<see cref="IdPattern"/>), so an entry that is not one is refused on the way
/// in and on the way out. That is what keeps a path, and with it the user's layout, out of the
/// file.</para>
/// </summary>
public sealed partial class ProviderSizeStore
{
    /// <summary>
    /// How many figures are kept per provider. With one a day (<see cref="ProviderSizeHistory"/>),
    /// that is two months of daily scans, and longer for a user who scans less often.
    /// </summary>
    public const int KeptPerProvider = 60;

    /// <summary>The format this build writes and reads. A file of any other is not read.</summary>
    private const int Format = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> None =
        ReadOnlyDictionary<string, IReadOnlyList<SizePoint>>.Empty;

    private readonly string _file;

    public ProviderSizeStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _file = Path.Combine(environment.LocalAppData, "Deguffer", "provider-sizes.json");
    }

    /// <summary>Whether <paramref name="id"/> has the form every provider's id has.</summary>
    public static bool IsProviderId(string id) => IdPattern().IsMatch(id);

    /// <summary>
    /// Each provider's figures, oldest first, or none where the file is missing, cannot be read, or is
    /// not wholly one this store would write.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> Load()
    {
        Stored? stored;

        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(LongPath.Extended(_file)), SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing before the first scan, refused, torn, or not this format at all.
            return None;
        }

        // The annotations are not enforced on what a dictionary or an array holds, so a null there is
        // refused here, as the rest of a file this store would not write is.
        if (stored is not { Format: Format }
            || stored.Providers.Values.Any(points => points is null || points.Any(point => point is null)))
        {
            return None;
        }

        var providers = stored.Providers.ToDictionary(
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

        try
        {
            var stored = new Stored(
                Format,
                providers.ToDictionary(entry => entry.Key, entry => entry.Value.Select(StoredPoint.Of).ToArray(), StringComparer.Ordinal));

            StoreFile.Replace(_file, JsonSerializer.Serialize(stored, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Remove the stored history. Returns whether none is left.</summary>
    public bool Remove()
    {
        try
        {
            File.Delete(LongPath.Extended(_file));

            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever stored.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held, or refused. It is still there.
            return false;
        }
    }

    /// <summary>
    /// Whether every entry is one this store writes: a provider's id, and its figures in order of
    /// time, each a UTC instant and a size no smaller than zero.
    /// </summary>
    private static bool Usable(IReadOnlyDictionary<string, SizePoint[]> providers) =>
        providers.All(entry => IsProviderId(entry.Key) && Ordered(entry.Value));

    private static bool Ordered(SizePoint[] points)
    {
        for (var at = 0; at < points.Length; at++)
        {
            if (points[at] is not { Bytes: >= 0, TakenUtc.Kind: DateTimeKind.Utc }
                || (at > 0 && points[at].TakenUtc <= points[at - 1].TakenUtc))
            {
                return false;
            }
        }

        return points.Length > 0;
    }

    /// <summary>A provider's id: lower-case letters, digits and hyphens, as every provider declares one.</summary>
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>The file as it is written.</summary>
    private sealed record Stored(int Format, Dictionary<string, StoredPoint[]> Providers);

    /// <summary>
    /// One figure as it is written: a class with every member a constructor parameter, so a figure
    /// missing any of them is refused rather than read as a zero.
    /// </summary>
    private sealed record StoredPoint(DateTime TakenUtc, long Bytes, bool Approximate, bool Elevated)
    {
        public static StoredPoint Of(SizePoint point) => new(point.TakenUtc, point.Bytes, point.Approximate, point.Elevated);

        public SizePoint ToPoint() => new(TakenUtc, Bytes, Approximate, Elevated);
    }
}
