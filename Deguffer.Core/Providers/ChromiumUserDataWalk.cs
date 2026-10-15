using System.Collections.Concurrent;
using Deguffer.Core.Exploring;
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
/// state, which <see cref="ChromiumUserDataProvider"/> classifies as a whole, so everything in there it
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
    /// <param name="tuning">What the walk runs with, resolved for the drive that holds <paramref name="root"/>.</param>
    public static ChromiumFolderWalk Under(
        string root,
        IReadOnlyList<string> notEntered,
        ScanTuner tuning,
        CancellationToken ct = default)
    {
        // Absent is a complete answer, and the walk below would report it as a refusal.
        if (LongPath.ProbeDirectory(root) is PathPresence.Absent)
        {
            return new ChromiumFolderWalk([], RootUnreadable: false);
        }

        var excluded = NotEntered(notEntered);
        var found = new ConcurrentBag<ChromiumFolder>();
        var rootUnreadable = false;

        // Each directory carries its depth, and reads its path from its listing. The only file named
        // is the marker, which is the only one asked about.
        BoundedFileWalk.Visit(
            LongPath.Extended(root),
            rootState: 0,
            tuning,
            FileNames.StoresAnd(ChromiumLayout.Browser.IdentifyingFile),
            (depth, contents, descend) =>
            {
                var path = LongPath.Display(contents.Directory);

                if (depth == 0)
                {
                    rootUnreadable = contents.WasRefused;
                }
                else if (!Enters(path, depth, Marked(path, contents), found))
                {
                    return;
                }

                // Asked once a listing, and of each child only where this folder holds one never entered.
                excluded.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var notHere);

                foreach (var child in contents.Entries)
                {
                    if (child.IsDirectory && notHere?.Contains(child.Name) != true)
                    {
                        descend(child, depth + 1);
                    }
                }
            },
            static () => { },
            TimeProvider.System,
            ct);

        return new ChromiumFolderWalk(
            rootUnreadable ? [] : [.. found.OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)],
            rootUnreadable);
    }

    /// <summary>
    /// <see cref="Under"/>, read from an Explore scan's tree rather than from the disk, where the
    /// scan listed the whole root (<see cref="ProtectionScope"/>). The same folders are found by the
    /// same rule, because the scan's tree holds every listing this walk would make: each directory's
    /// files and folders, which of them are links, and which folders refused to be listed.
    ///
    /// <para>The tree is as the scan found the disk, which is the moment a walk started beside the
    /// scan would have looked too. A folder created after the scan is found by the next one.</para>
    /// </summary>
    /// <param name="tree">The scan's tree.</param>
    /// <param name="root">The node of <paramref name="tree"/> that is the application-data root, listed whole.</param>
    /// <param name="notEntered">As <see cref="Under"/> takes it.</param>
    public static ChromiumFolderWalk In(
        ExploreTree tree,
        int root,
        IReadOnlyList<string> notEntered,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var excluded = NotEntered(notEntered);
        var found = new ConcurrentBag<ChromiumFolder>();
        var pending = new Stack<(int Node, string Path, int Depth)>();

        pending.Push((root, LongPath.Display(tree.PathOf(root)), 0));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            if (directory.Depth > 0
                && !Enters(directory.Path, directory.Depth, Marked(tree, directory.Node, directory.Path, directory.Depth), found))
            {
                continue;
            }

            excluded.TryGetValue(Path.TrimEndingDirectorySeparator(directory.Path), out var notHere);

            foreach (var child in tree.ChildrenOf(directory.Node))
            {
                // A link is not entered, as the walk declines to descend through one.
                if (!tree.IsDirectory(child) || tree.IsLink(child))
                {
                    continue;
                }

                var name = tree.NameOf(child);

                if (notHere?.Contains(name) != true)
                {
                    pending.Push((child, Path.Combine(directory.Path, name), directory.Depth + 1));
                }
            }
        }

        return new ChromiumFolderWalk(
            [.. found.OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)],
            RootUnreadable: false);
    }

    /// <summary>
    /// Whether a directory below the root is entered, adding it to <paramref name="found"/> where it
    /// is a Chromium user-data folder. The one rule both walks follow.
    /// </summary>
    /// <param name="marker">Whether the directory holds <c>Local State</c>.</param>
    private static bool Enters(string path, int depth, PathPresence marker, ConcurrentBag<ChromiumFolder> found)
    {
        var webView2 = Path.GetFileName(path).Equals(WebView2FolderName, StringComparison.OrdinalIgnoreCase);

        if (marker is not PathPresence.Absent)
        {
            // Identified only on the file itself, since a refusal is no evidence of a folder that
            // holds it. Either way nothing below is entered.
            if ((depth == 1 || webView2) && marker is PathPresence.Present)
            {
                found.Add(new ChromiumFolder(path, webView2));
            }

            return false;
        }

        // An EBWebView without the marker has nothing inside that could be looked for.
        return !webView2 && depth < WebView2Depth;
    }

    /// <summary>
    /// The folders never entered, as the names never entered in each folder holding one, so both walks
    /// ask one lookup of a folder and build no child's path to ask it. A folder is keyed in display
    /// form without a trailing separator. The short form is what Windows puts in TEMP on a profile
    /// with a long folder name, and a listing reports long names, so both sides are compared
    /// unaliased.
    /// </summary>
    private static Dictionary<string, HashSet<string>> NotEntered(IReadOnlyList<string> notEntered)
    {
        var byParent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in notEntered.Select(path => Path.TrimEndingDirectorySeparator(LongPath.Unaliased(path))))
        {
            // A volume's root has no parent, so no walk meets it as a child.
            if (Path.GetDirectoryName(path) is not { } parent)
            {
                continue;
            }

            var key = Path.TrimEndingDirectorySeparator(parent);

            if (!byParent.TryGetValue(key, out var names))
            {
                byParent[key] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            names.Add(Path.GetFileName(path));
        }

        return byParent;
    }

    /// <summary>
    /// <see cref="Marked(string, DirectoryContents)"/>, read from the scan's tree: any file the scan
    /// listed, a link or not, and a probe by name where the folder refused its listing.
    ///
    /// <para><b>Where the folder could be identified, a marker the tree lacks is asked of the disk.</b>
    /// The engine replaces <c>Local State</c> by writing a new file and renaming it over the old one.
    /// A listing sees one name or the other throughout, but a file table is read record by record over
    /// minutes, and can meet the new record under its temporary name and the old one once freed, so
    /// the tree holds neither. Missing it there would leave the folder undeclared, and its sign-ins
    /// open to removal. The probe costs one file per folder beside the root and per
    /// <c>EBWebView</c>, against the listing of every directory it replaces.</para>
    /// </summary>
    private static PathPresence Marked(ExploreTree tree, int directory, string path, int depth)
    {
        var marker = Path.Combine(path, ChromiumLayout.Browser.IdentifyingFile);

        if (tree.ListingWasRefused(directory))
        {
            return LongPath.ProbeFile(marker);
        }

        foreach (var child in tree.ChildrenOf(directory))
        {
            if (!tree.IsDirectory(child)
                && tree.NameSpanOf(child).Equals(ChromiumLayout.Browser.IdentifyingFile, StringComparison.OrdinalIgnoreCase))
            {
                return PathPresence.Present;
            }
        }

        return depth == 1 || Path.GetFileName(path).Equals(WebView2FolderName, StringComparison.OrdinalIgnoreCase)
            ? LongPath.ProbeFile(marker)
            : PathPresence.Absent;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds <c>Local State</c>, read from the listing the walk
    /// already made. A directory that refused the listing is probed by name instead, because a full
    /// path can resolve where the directory cannot be listed, and a user-data folder like that is
    /// still one.
    /// </summary>
    private static PathPresence Marked(string directory, DirectoryContents listed) => listed.WasRefused
        ? LongPath.ProbeFile(Path.Combine(directory, ChromiumLayout.Browser.IdentifyingFile))
        : listed.Entries.Any(entry => !entry.IsDirectory && IsMarker(entry)) || listed.ReparseFiles.Any(IsMarker)
            ? PathPresence.Present
            : PathPresence.Absent;

    // A file listed without a name is not the marker: the walk names the marker wherever it lists it.
    private static bool IsMarker(WalkEntry file) =>
        file.HasName && file.Name.Equals(ChromiumLayout.Browser.IdentifyingFile, StringComparison.OrdinalIgnoreCase);
}
