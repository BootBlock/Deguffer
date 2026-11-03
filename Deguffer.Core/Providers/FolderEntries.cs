using System.IO.Enumeration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every entry directly inside one folder, files included, for a provider whose recognised children
/// are files.
///
/// <para><see cref="ChildDirectories"/> lists directories only, because a §5.2 declaration classifies
/// directories. A folder whose disposable children are files — an editor's handshake lock, a
/// messaging key — has to have its files listed too, and has to keep the same two facts that type
/// keeps: a folder that is not there holds nothing, and a folder that refused to be listed is not
/// empty.</para>
///
/// <para>Materialised rather than streamed, because a listing that fails partway must yield nothing.
/// A caller that had already acted on the first half would describe a folder nobody fully read. Most
/// folders read this way hold at most a few thousand entries, one per game in Steam's library
/// artwork, not the hundreds of thousands G4 is about. A developer's temporary folder is the
/// exception, so the rows that read it share one reading per pass through
/// <see cref="TempFolderListings"/>, which keeps only what each entry is recognised by.</para>
/// </summary>
internal static class FolderEntries
{
    /// <summary>
    /// The entries, empty where the folder is not there, and null where it refused to be listed.
    /// </summary>
    public static IReadOnlyList<FileSystemInfo>? Of(string folder) =>
        Of(folder, static (ref FileSystemEntry entry) => entry.ToFileSystemInfo());

    /// <summary>
    /// Each entry as <paramref name="transform"/> reads it, on the same terms as <see cref="Of(string)"/>,
    /// for a caller that keeps less of an entry than a <see cref="FileSystemInfo"/>.
    /// </summary>
    public static IReadOnlyList<T>? Of<T>(string folder, FileSystemEnumerable<T>.FindTransform transform)
    {
        var entries = new List<T>();

        using var listing = new DirectoryListing<T>(folder, transform);

        while (listing.MoveNext())
        {
            entries.Add(listing.Current);
        }

        return listing.Outcome switch
        {
            PathPresence.Present => entries,
            PathPresence.Absent => [],

            // §5.3 makes a refusal ordinary. It is still not an empty folder, and the caller says so.
            _ => null,
        };
    }
}
