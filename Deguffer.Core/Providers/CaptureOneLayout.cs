using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one walk of a session's folders found.</summary>
/// <param name="Sidecars">Every <c>CaptureOne</c> folder in the session, by full path.</param>
/// <param name="Links">
/// Every link the walk met and did not follow. A link named <c>CaptureOne</c> is among them, and is
/// never a sidecar: what it points at was never classified.
/// </param>
/// <param name="Unreadable">Folders that would not be listed, so nothing below them was searched.</param>
public sealed record CaptureOneSessionWalk(
    IReadOnlyList<string> Sidecars,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Unreadable);

/// <summary>
/// How Capture One lays out a catalog and a session on Windows, and the one child of each that is
/// its cache.
///
/// <para><b>A catalog</b> is a folder named <c>&lt;Name&gt;.cocatalog</c> holding its database,
/// <c>&lt;Name&gt;.cocatalogdb</c>, beside <c>Cache</c>. <b>A session</b> is a folder holding
/// <c>&lt;Name&gt;.cosessiondb</c>, and Capture One gives every folder of images it shows in one a
/// <c>CaptureOne</c> folder of its own, holding <c>Cache</c> beside the settings folders.</para>
///
/// <para><b>§5.2, and the worst case here is total.</b> <c>Cache</c> is the only child either
/// layout recognises. Beside it in a catalog sit <c>Originals</c>, which in a catalog that imported
/// its images <em>is</em> the user's photographs, and <c>Adjustments</c>, which holds masks. Beside
/// it in a sidecar sit <c>Settings120</c>, <c>Settings166</c> and their kin, which are every
/// adjustment the user has made: Capture One never writes to a raw file, so those small files are
/// the edits. Their number follows the rendering engine and keeps changing, which is why the rule
/// names what is recognised and never what to avoid.</para>
///
/// <para><b>Nothing under <c>%LOCALAPPDATA%\CaptureOne</c> or <c>%APPDATA%\Capture One</c> is
/// offered.</b> Those hold styles, presets and preferences, and no cache.</para>
/// </summary>
public static class CaptureOneLayout
{
    /// <summary>The one child of a catalog or a sidecar that is disposable.</summary>
    public const string CacheFolderName = "Cache";

    /// <summary>The folder Capture One adds to every folder of images a session shows.</summary>
    public const string SidecarFolderName = "CaptureOne";

    /// <summary>A catalog's database, inside its package.</summary>
    public const string CatalogDatabaseExtension = ".cocatalogdb";

    /// <summary>A session's database, in the session's own folder.</summary>
    public const string SessionDatabaseExtension = ".cosessiondb";

    /// <summary>
    /// The file Capture One writes into a catalog while it has it open. A crash leaves it behind, so
    /// its existence proves nothing, and it is handed to the live-tree inspector to ask whether
    /// anything holds it.
    /// </summary>
    public const string CatalogWriteLock = "writelock";

    /// <summary>The program's process name, as the process table reports it.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["CaptureOne"];

    public static bool IsCache(string name) => name.Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase);

    public static bool IsSidecar(string name) => name.Equals(SidecarFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The files in <paramref name="entries"/> whose extension is <paramref name="extension"/>.</summary>
    public static IReadOnlyList<string> Databases(IReadOnlyList<FileSystemInfo> entries, string extension) =>
    [
        .. entries
            .Where(entry => entry is FileInfo
                && entry.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Select(entry => LongPath.Display(entry.FullName)),
    ];

    /// <summary>
    /// Why an entry beside a catalog's <c>Cache</c> must survive, written for the user. Every entry
    /// there is named, whether or not Deguffer knows it: an unrecognised one is Tier 4.
    /// </summary>
    public static string CatalogSurvivorReason(FileSystemInfo entry) => entry.Name switch
    {
        _ when entry.Extension.Equals(CatalogDatabaseExtension, StringComparison.OrdinalIgnoreCase) =>
            "The catalog's database: every image it holds, and every edit, rating and keyword.",
        _ when entry.Name.Equals("Originals", StringComparison.OrdinalIgnoreCase) =>
            "Your photographs, imported into the catalog. This is the only copy of many of them.",
        _ when entry.Name.Equals("Adjustments", StringComparison.OrdinalIgnoreCase) =>
            "Your masks and the adjustments made through them.",
        _ when entry.Name.Equals(CatalogWriteLock, StringComparison.OrdinalIgnoreCase) =>
            "Capture One's mark that the catalog is open.",
        _ => "Part of the catalog rather than its cache, so it is left alone.",
    };

    /// <summary>
    /// Why an entry beside a sidecar's <c>Cache</c> must survive, written for the user, on the same
    /// terms as <see cref="CatalogSurvivorReason"/>.
    /// </summary>
    public static string SidecarSurvivorReason(FileSystemInfo entry) =>
        entry.Name.StartsWith("Settings", StringComparison.OrdinalIgnoreCase)
            ? "Your adjustments to the images in this folder. Capture One never changes a raw file, "
                + "so these are the edits themselves."
            : "Part of Capture One's record of these images rather than its cache, so it is left alone.";

    /// <summary>
    /// Every <c>CaptureOne</c> folder in the session at <paramref name="session"/>.
    ///
    /// <para>Iterative, because a session's folders can nest deeply. A <c>CaptureOne</c> folder is
    /// not entered: everything below it is Capture One's, and a folder of images never sits there.
    /// A link is never followed, because it points at a tree the session does not own.</para>
    ///
    /// <para>Only the session's own folder is walked. A folder of images elsewhere that the session
    /// lists as a favourite has a sidecar too, and is not reached.</para>
    /// </summary>
    public static CaptureOneSessionWalk WalkSession(string session, CancellationToken ct = default)
    {
        var sidecars = new List<string>();
        var links = new List<string>();
        var unreadable = new List<string>();
        var pending = new Stack<string>();
        pending.Push(LongPath.Extended(session));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            var scan = ChildDirectories.Under(directory);

            if (scan.Unreadable)
            {
                unreadable.Add(LongPath.Display(directory));
                continue;
            }

            links.AddRange(scan.Links.Select(link => LongPath.Display(link.FullName)));

            foreach (var child in scan.Directories)
            {
                if (IsSidecar(child.Name))
                {
                    sidecars.Add(LongPath.Display(child.FullName));
                }
                else
                {
                    pending.Push(child.FullName);
                }
            }
        }

        return new CaptureOneSessionWalk(sidecars, links, unreadable);
    }
}
