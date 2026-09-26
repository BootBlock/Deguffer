using System.Collections.Concurrent;
using Deguffer.Core.Safety;

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
/// <item>A link. What it points at was never identified, as for the one-level walk this replaced.
/// </item>
/// <item>A Chromium user-data folder, <c>EBWebView</c> or not. Its children are its own profiles and
/// state, which <see cref="ChromiumCacheProvider"/> classifies as a whole, so everything in there it
/// does not recognise is Tier 4 (§5.2). Finding a second folder inside one would let a deletion
/// reach into a child the first folder's rules declined. Edge keeps a WebView2 folder for its sign-in
/// inside its own user data, and this is why it stays unreached. A marker Windows would not describe
/// is treated as a boundary too, since the folder may be one.</item>
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

    /// <summary>
    /// Listing a directory waits on the disk, so the directories at one depth are listed side by side.
    /// Bounded as <see cref="Scanning.BoundedFileWalk"/> bounds its own (G4).
    /// </summary>
    private static readonly int Parallelism = Math.Min(Environment.ProcessorCount * 2, 16);

    /// <param name="root">An application-data root.</param>
    /// <param name="temporaryFolder">The user's temporary folder, which is never entered.</param>
    public static ChromiumFolderWalk Under(string root, string temporaryFolder, CancellationToken ct = default)
    {
        var top = ChildDirectories.Under(root);

        if (top.Unreadable)
        {
            return new ChromiumFolderWalk([], RootUnreadable: true);
        }

        // The short form is what Windows puts in TEMP on a profile with a long folder name, and the
        // listing reports long names, so both sides are compared unaliased.
        var temporary = Path.TrimEndingDirectorySeparator(LongPath.Unaliased(temporaryFolder));
        var found = new ConcurrentBag<ChromiumFolder>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

        IReadOnlyList<string> level = Outside(top.Directories, temporary);

        for (var depth = 1; level.Count > 0; depth++)
        {
            var next = new ConcurrentBag<string>();
            var atDepth = depth;

            Parallel.ForEach(level, options, path =>
            {
                var webView2 = Path.GetFileName(path).Equals(WebView2FolderName, StringComparison.OrdinalIgnoreCase);
                var marker = Path.Combine(path, ChromiumLayout.Browser.IdentifyingFile);

                if (LongPath.FileMayExist(marker))
                {
                    // Identified only on the file itself, since a refusal is no evidence of a folder
                    // that holds it. Either way nothing below is entered.
                    if ((atDepth == 1 || webView2) && LongPath.FileExists(marker))
                    {
                        found.Add(new ChromiumFolder(path, webView2));
                    }

                    return;
                }

                // An EBWebView without the marker has nothing inside that could be looked for.
                if (webView2 || atDepth == WebView2Depth)
                {
                    return;
                }

                foreach (var child in Outside(ChildDirectories.Under(path).Directories, temporary))
                {
                    next.Add(child);
                }
            });

            level = [.. next];
        }

        return new ChromiumFolderWalk(
            [.. found.OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)],
            RootUnreadable: false);
    }

    /// <summary>The directories as display paths, less the temporary folder.</summary>
    private static List<string> Outside(IReadOnlyList<DirectoryInfo> directories, string temporary) =>
    [
        .. directories
            .Select(directory => LongPath.Display(directory.FullName))
            .Where(path => !Path.TrimEndingDirectorySeparator(path).Equals(temporary, StringComparison.OrdinalIgnoreCase)),
    ];
}
