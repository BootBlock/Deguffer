using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>A Chromium user-data folder below an application-data root, and which rule found it.</summary>
/// <param name="Path">The folder, in display form.</param>
/// <param name="IsWebView2">
/// Whether it is a WebView2 folder, found by its name below the root, rather than a direct child of
/// the root.
/// </param>
public readonly record struct ChromiumFolder(string Path, bool IsWebView2);

/// <param name="Folders">Every folder found, ordered by path so a plan lists them the same way each time.</param>
/// <param name="RootUnreadable">
/// The root itself refused to be listed, so <paramref name="Folders"/> is empty because nothing was
/// looked at, not because nothing is there.
/// </param>
public sealed record ChromiumFolderWalk(IReadOnlyList<ChromiumFolder> Folders, bool RootUnreadable);

/// <summary>
/// Where the Chromium user-data folders below one application-data root are: each direct child of
/// the root holding <c>Local State</c>, and each directory named <c>EBWebView</c> holding it within
/// <see cref="WebView2Depth"/> levels.
///
/// <para><b>WebView2 is found by the folder's name, and it still has to pass the positive
/// test.</b> The runtime puts its user-data folder in an <c>EBWebView</c> directory inside whatever
/// folder the host application chose, and hosts choose anywhere: 19 were measured on one workstation
/// between two and six levels below the root, 14 of them outside <c>Packages</c>. So no table of
/// vendors could reach them, and the name is the one fact they share. It says only where to look.
/// A directory of that name without <c>Local State</c> is not identified.</para>
///
/// <para><b>What bounds the walk, and why each bound is there.</b> Every directory is listed that
/// could hold such a folder above the depth bound, which is the cost of finding a folder whose
/// place nobody declares. Three things are never entered:</para>
/// <list type="bullet">
/// <item>A link. What it points at was never identified, and <see cref="BoundedFileWalk"/> holds
/// that rule itself.</item>
/// <item>A Chromium user-data folder, <c>EBWebView</c> or not. Its children are its own profiles and
/// state, which <see cref="ChromiumCacheProvider"/> classifies as a whole, so everything in there it
/// does not recognise is Tier 4 (§5.2). Finding a second folder inside one would let a deletion
/// reach into a child the first folder's rules declined. Edge keeps a WebView2 folder for its sign-in
/// inside its own user data, and this is why it stays unreached. A marker Windows would not describe
/// is treated as a boundary too, since the folder may be one. A declared host whose folder another
/// file marks, as Battle.net's <c>LocalPrefs.json</c> does, is named by the caller instead.</item>
/// <item>The temporary folder. It is not application data, it is the temporary files row's, and it
/// held 108,379 of the 129,815 directories within six levels of <c>%LOCALAPPDATA%</c> on the
/// measured workstation.</item>
/// </list>
///
/// <para>A directory below the root that will not be listed is passed over without a word. The walk
/// is choosing which applications to look at, not classifying a tool root's children, and a host it
/// could not see keeps its cache, which is the safe direction. Saying so would put a sentence on every
/// plan: <c>%LOCALAPPDATA%\ElevatedDiagnostics</c> refuses an ordinary account on an ordinary machine.
/// A root that will not be listed is different, because then nothing at all was looked at, and it is
/// reported.</para>
/// </summary>
public static class ChromiumUserDataWalk
{
    /// <summary>The directory WebView2 creates for its user data inside the folder a host chose.</summary>
    public const string WebView2FolderName = "EBWebView";

    /// <summary>
    /// How far below the root an <c>EBWebView</c> directory is looked for. The deepest measured sat
    /// six levels down, in a packaged application's <c>LocalCache</c>. A host deeper than this is
    /// unreached, which reclaims nothing rather than deleting anything.
    /// </summary>
    public const int WebView2Depth = 6;

    /// <param name="root">An application-data root.</param>
    /// <param name="notEntered">
    /// Folders that are never entered or identified: the user's temporary folder, and each declared
    /// host's own folder, which the caller classifies as a whole.
    /// </param>
    public static ChromiumFolderWalk Under(string root, IReadOnlyList<string> notEntered, CancellationToken ct = default)
    {
        // Absent is a complete answer, and the walk below would report it as a refusal.
        if (LongPath.ProbeDirectory(root) is PathPresence.Absent)
        {
            return new ChromiumFolderWalk([], RootUnreadable: false);
        }

        // The short form is what Windows puts in TEMP on a profile with a long folder name, and the
        // listing reports long names, so both sides are compared unaliased.
        var excluded = notEntered
            .Select(path => Path.TrimEndingDirectorySeparator(LongPath.Unaliased(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentBag<ChromiumFolder>();
        var rootUnreadable = false;

        BoundedFileWalk.Visit<(string Path, int Depth)>(
            LongPath.Extended(root),
            (root, 0),
            (directory, contents, descend) =>
            {
                if (directory.Depth == 0)
                {
                    rootUnreadable = contents.WasRefused;
                }
                else if (!Enters(directory.Path, directory.Depth, contents))
                {
                    return;
                }

                foreach (var child in contents.Entries.OfType<DirectoryInfo>())
                {
                    var path = LongPath.Display(child.FullName);

                    if (!excluded.Contains(Path.TrimEndingDirectorySeparator(path)))
                    {
                        descend(child, (path, directory.Depth + 1));
                    }
                }

                bool Enters(string path, int depth, DirectoryContents listed)
                {
                    var webView2 = Path.GetFileName(path).Equals(WebView2FolderName, StringComparison.OrdinalIgnoreCase);

                    var marker = Marked(path, listed);

                    if (marker is not PathPresence.Absent)
                    {
                        // Identified only on the file itself, since a refusal is no evidence of a
                        // folder that holds it. Either way nothing below is entered.
                        if ((depth == 1 || webView2) && marker is PathPresence.Present)
                        {
                            found.Add(new ChromiumFolder(path, webView2));
                        }

                        return false;
                    }

                    // An EBWebView without the marker has nothing inside that could be looked for.
                    return !webView2 && depth < WebView2Depth;
                }
            },
            static () => { },
            ct);

        return new ChromiumFolderWalk(
            rootUnreadable ? [] : [.. found.OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)],
            rootUnreadable);
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds <c>Local State</c>, read from the listing the walk
    /// already made. A directory that refused the listing is probed by name instead, because a full
    /// path can resolve where the directory cannot be listed, and a user-data folder like that is
    /// still one.
    /// </summary>
    private static PathPresence Marked(string directory, DirectoryContents listed) => listed.WasRefused
        ? LongPath.ProbeFile(Path.Combine(directory, ChromiumLayout.Browser.IdentifyingFile))
        : listed.Entries.OfType<FileInfo>().Concat(listed.ReparseFiles).Any(IsMarker)
            ? PathPresence.Present
            : PathPresence.Absent;

    private static bool IsMarker(FileInfo file) =>
        file.Name.Equals(ChromiumLayout.Browser.IdentifyingFile, StringComparison.OrdinalIgnoreCase);
}
