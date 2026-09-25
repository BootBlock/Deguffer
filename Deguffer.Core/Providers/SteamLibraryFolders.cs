using System.Globalization;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What became of Steam's own list of its game libraries.</summary>
public enum SteamLibraryListing
{
    /// <summary>Read and understood. The libraries it names are all known.</summary>
    Read,

    /// <summary>
    /// Not there. A list that does not exist names no library, so this is a complete answer: the only
    /// library known is the one beside the program.
    /// </summary>
    Absent,

    /// <summary>
    /// There, or possibly there, and not read: Windows refused it, something held it open, or it is
    /// larger than any list Steam writes. Any library other than the one beside the program is
    /// unknown.
    /// </summary>
    Unreadable,

    /// <summary>
    /// Read, and not understood: not well-formed, or not a list of libraries. Any library other than
    /// the one beside the program is unknown.
    /// </summary>
    Malformed,

    /// <summary>
    /// Read and understood, and naming at least one library Deguffer could not place: an entry with
    /// no path, or with one that is not a full path. The libraries it could place are known, and the
    /// one it could not may hold anything.
    /// </summary>
    Unplaced,
}

/// <summary>Every Steam library this machine's Steam knows of, and how that was established.</summary>
/// <param name="Folders">
/// Each library's own folder, the install directory first, with no repeats. Always holds at least the
/// install directory, which is a library whether or not the list names it.
/// </param>
/// <param name="Listing">What became of the list the other libraries were read from.</param>
/// <param name="ListPath">Where that list is, for a sentence about it.</param>
public sealed record SteamLibraries(IReadOnlyList<string> Folders, SteamLibraryListing Listing, string ListPath)
{
    /// <summary>
    /// Whether <see cref="Folders"/> is every library Steam knows of. False where the list could not
    /// be read, and a claim about "every library" would then be a claim about the ones that happened to
    /// be found.
    /// </summary>
    public bool IsComplete => Listing is SteamLibraryListing.Read or SteamLibraryListing.Absent;
}

/// <summary>
/// Finds Steam's game libraries by reading the list Steam keeps of them.
///
/// <para><b>A library can be on any drive, and only Steam's list says where.</b> A user adds a library
/// by picking a folder, usually on a second drive with room for games, and Steam records the choice in
/// <c>steamapps\libraryfolders.vdf</c> under the install directory. Nothing else on the machine
/// records it, so the list is read rather than any drive searched: a search would find folders that
/// merely look like libraries, and miss one on a drive nobody thought to search.</para>
///
/// <para>Both of the list's layouts are read. The current one gives each library a numbered block
/// holding a <c>path</c>. Older clients wrote the path as the numbered entry's own value, beside
/// entries such as <c>TimeNextStatsReport</c> that are not libraries, which is why only numbered keys
/// are read.</para>
///
/// <para><b>A recorded library is not required to carry a marker</b>, as the install directory is
/// required to carry <see cref="SteamDiscovery.RootMarker"/>. What a caller does with a library is
/// look for <c>steamapps\shadercache</c> in it and classify that folder's children by name, so a stale
/// entry naming a folder that is no longer a library finds nothing to classify. A marker would guard
/// against a path shape nothing else produces, and a library emptied of games but still holding its
/// caches has no marker to find.</para>
/// </summary>
public static class SteamLibraryFolders
{
    /// <summary>The list's name, under the install directory's <c>steamapps</c>.</summary>
    public const string ListFileName = "libraryfolders.vdf";

    /// <summary>
    /// Far past any list Steam writes. Each library block also lists the id and size of every game in
    /// it, one short line a game, so this allows tens of thousands of games.
    /// </summary>
    private const int MaximumBytes = 2 * 1024 * 1024;

    /// <summary>Every library of the Steam installed at <paramref name="install"/>.</summary>
    public static SteamLibraries Of(string install)
    {
        var list = Path.Combine(install, "steamapps", ListFileName);

        switch (LongPath.ProbeFile(list))
        {
            case PathPresence.Absent:
                return new SteamLibraries([install], SteamLibraryListing.Absent, list);

            case PathPresence.Refused:
                return new SteamLibraries([install], SteamLibraryListing.Unreadable, list);
        }

        // Too large, locked, or gone since the probe. Read as unreadable rather than absent, because
        // something was there and nothing established what it said.
        if (BoundedFile.Read(list, MaximumBytes) is not { } content)
        {
            return new SteamLibraries([install], SteamLibraryListing.Unreadable, list);
        }

        if (SteamKeyValues.Parse(Encoding.UTF8.GetString(content.Span)) is not { } entries
            || entries.FirstOrDefault(e => string.Equals(e.Key, "libraryfolders", StringComparison.OrdinalIgnoreCase))
                is not { Value: null } root)
        {
            return new SteamLibraries([install], SteamLibraryListing.Malformed, list);
        }

        var folders = new List<string> { install };
        var unplaced = false;

        foreach (var entry in root.Children.Where(e => IsLibraryIndex(e.Key)))
        {
            // Configured refuses a relative path, which would otherwise resolve against Deguffer's own
            // working directory, and normalises the rest so a library written with a trailing
            // separator is not counted twice. A refused entry is still a library Steam named, so the
            // list stops being a claim about every library rather than losing one in silence.
            if (LongPath.Configured(entry.Value ?? entry.Child("path")?.Value) is not { } folder)
            {
                unplaced = true;
            }
            else if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        return new SteamLibraries(folders, unplaced ? SteamLibraryListing.Unplaced : SteamLibraryListing.Read, list);
    }

    /// <summary>
    /// Whether a key is a library's number. Steam numbers its libraries from zero, and the older
    /// layout keeps other settings beside them under names that are not numbers.
    /// </summary>
    private static bool IsLibraryIndex(string key) =>
        uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
