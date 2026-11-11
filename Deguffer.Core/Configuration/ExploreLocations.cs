using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The places Explore remembers between sessions: the computers added by name in the network
/// browser, the folders scanned recently, and where the browser was last left.
///
/// <para>Stored rather than held for the session, because each answers "the places I use": a NAS
/// discovery cannot find is added once, not every launch. None of it changes what a scan reads or
/// what may be removed. Each is only somewhere the Path… box can be filled from, and the page's own
/// check still asks whether it is a folder.</para>
///
/// <para>A value, as <see cref="ProviderChoice"/> is, so the preferences compare equal when nothing
/// has changed. A hand-edited entry that names nothing is dropped as the file is read: the
/// serialiser checks the lists, not what they hold.</para>
/// </summary>
public sealed record ExploreLocations
{
    /// <summary>How many recent folders are kept. Enough for the places a person comes back to, few enough to read at a glance.</summary>
    public const int RecentKept = 8;

    public static readonly ExploreLocations None = new();

    /// <summary>The computers added by name, A to Z, each once, case aside.</summary>
    public IReadOnlyList<string> Computers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            field =
            [
                .. value
                    .Select(name => name is null ? null : NetworkNames.ComputerName(name))
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
        }
    } = [];

    /// <summary>The folders scanned recently, most recent first, each once, case aside.</summary>
    public IReadOnlyList<string> Recent
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            field =
            [
                .. value
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(RecentKept),
            ];
        }
    } = [];

    /// <summary>The root the browser was last left under, which is where it finds its way back from.</summary>
    public NetworkRoot? LastRoot { get; init; }

    /// <summary>The place the browser was last left at, or null where it has not been opened.</summary>
    public string? LastPlace { get; init; }

    /// <summary>These locations with <paramref name="computer"/> added. The name is one <see cref="NetworkNames.ComputerName"/> gave.</summary>
    public ExploreLocations WithComputer(string computer) => this with { Computers = [.. Computers, computer] };

    /// <summary>These locations with <paramref name="computer"/> taken out.</summary>
    public ExploreLocations WithoutComputer(string computer) =>
        this with { Computers = [.. Computers.Where(name => !name.Equals(computer, StringComparison.OrdinalIgnoreCase))] };

    /// <summary>
    /// These locations once a scan of <paramref name="target"/> has started: its folder the most
    /// recent, and the oldest dropped past <see cref="RecentKept"/>. A whole drive is not kept, because
    /// the drive box already offers every drive.
    /// </summary>
    public ExploreLocations Scanned(Exploring.ExploreTarget target) =>
        target.Folder is { } folder ? this with { Recent = [folder, .. Recent] } : this;

    /// <summary>These locations with the browser left at <paramref name="place"/> under <paramref name="root"/>.</summary>
    public ExploreLocations LeftAt(NetworkRoot root, string place) => this with { LastRoot = root, LastPlace = place };

    public bool Equals(ExploreLocations? other) =>
        other is not null
        && Computers.SequenceEqual(other.Computers, StringComparer.Ordinal)
        && Recent.SequenceEqual(other.Recent, StringComparer.Ordinal)
        && LastRoot == other.LastRoot
        && string.Equals(LastPlace, other.LastPlace, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Computers.Count, Recent.Count, LastRoot);
}
