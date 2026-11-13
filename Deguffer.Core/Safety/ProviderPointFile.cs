using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Safety;

/// <summary>
/// The file of a store that keeps figures for each provider, named by the provider's id and listed
/// oldest first, inside an envelope that states the format it was written in. The size history (#358)
/// and the planning times (#443) are both one.
///
/// <para><b>A file that cannot be read is read as none, whole.</b> A missing file, a torn write, a hand
/// edit and a format this build does not know all arrive as no history, never as part of one. The
/// caller then checks the figures themselves through <see cref="ProviderPointFile.AreUsable"/>, on the
/// way in and on the way out, so a store never writes a file it would not read.</para>
/// </summary>
/// <typeparam name="TStored">One figure as it is written.</typeparam>
internal sealed class ProviderPointFile<TStored>(string file, int format, JsonTypeInfo<ProviderPoints<TStored>> contract)
    where TStored : class
{
    /// <summary>
    /// Each provider's figures as written, or null where the file is missing, cannot be read, is of
    /// another format, or holds a null where a list or a figure belongs.
    /// </summary>
    public Dictionary<string, TStored[]>? Read()
    {
        ProviderPoints<TStored>? stored;

        try
        {
            stored = JsonSerializer.Deserialize(StoreFile.Read(file), contract);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing before the first write, refused, torn, or not this format at all.
            return null;
        }

        // The annotations are not enforced on what a dictionary or an array holds, so a null there is
        // refused here, as the rest of a file this store would not write is.
        if (stored is null
            || stored.Format != format
            || stored.Providers.Values.Any(points => points is null || points.Any(point => point is null)))
        {
            return null;
        }

        return stored.Providers;
    }

    /// <summary>Write <paramref name="providers"/> in place of what is stored. Returns whether it was written.</summary>
    public bool Write(Dictionary<string, TStored[]> providers)
    {
        try
        {
            StoreFile.Replace(file, JsonSerializer.Serialize(new ProviderPoints<TStored>(format, providers), contract));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Remove the file. Returns whether none is left.</summary>
    public bool Remove()
    {
        try
        {
            File.Delete(LongPath.Extended(file));

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
}

/// <summary>The file as it is written: its format, and each provider's figures.</summary>
internal sealed record ProviderPoints<TStored>(int Format, Dictionary<string, TStored[]> Providers);

/// <summary>What every <see cref="ProviderPointFile{TStored}"/> holds its figures to.</summary>
internal static partial class ProviderPointFile
{
    /// <summary>
    /// Whether <paramref name="id"/> has the form every provider's id has. Nothing but an id names an
    /// entry, which is what keeps a path, and with it the user's layout, out of every such file.
    /// </summary>
    public static bool IsProviderId(string id) => IdPattern().IsMatch(id);

    /// <summary>
    /// Whether every entry is one a store writes: a provider's id, and at least one figure, each well
    /// formed and taken at a UTC instant later than the one before it.
    /// </summary>
    public static bool AreUsable<TPoint>(
        IReadOnlyDictionary<string, TPoint[]> providers,
        Func<TPoint, DateTime> takenUtc,
        Func<TPoint, bool> wellFormed) =>
        providers.All(entry => IsProviderId(entry.Key) && Ordered(entry.Value, takenUtc, wellFormed));

    private static bool Ordered<TPoint>(TPoint[] points, Func<TPoint, DateTime> takenUtc, Func<TPoint, bool> wellFormed)
    {
        for (var at = 0; at < points.Length; at++)
        {
            var taken = takenUtc(points[at]);

            if (!wellFormed(points[at])
                || taken.Kind is not DateTimeKind.Utc
                || (at > 0 && taken <= takenUtc(points[at - 1])))
            {
                return false;
            }
        }

        return points.Length > 0;
    }

    /// <summary>A provider's id: lower-case letters, digits and hyphens, as every provider declares one.</summary>
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
