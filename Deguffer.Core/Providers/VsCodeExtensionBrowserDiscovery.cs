using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One Puppeteer browser cache an editor extension keeps in its own storage, as found on disk.</summary>
/// <param name="Editor">The editor whose user-data folder holds the extension's storage.</param>
/// <param name="Extension">
/// The extension's storage folder's own name, which is the extension's identifier
/// (<c>saoudrizwan.claude-dev</c>): the editor names the folder after it.
/// </param>
/// <param name="ExtensionFolder">That folder, in display form.</param>
/// <param name="Root">The cache root inside it, in display form.</param>
/// <param name="Survey">What the cache's two levels hold.</param>
internal sealed record ExtensionBrowserCache(
    VsCodeUserData Editor,
    string Extension,
    string ExtensionFolder,
    string Root,
    PuppeteerCacheSurvey Survey);

/// <summary>What a search found, and what it met on the way and did not look past.</summary>
/// <param name="Declined">Every link met on the way down, and every path Windows would not describe, for §5.6.</param>
/// <param name="Unreadable">Whether a folder on the way would not be listed or described.</param>
internal sealed record ExtensionBrowserSearch(
    IReadOnlyList<ExtensionBrowserCache> Caches,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable);

/// <summary>
/// Finds the Puppeteer browser caches that editor extensions keep in their global storage, in every
/// Code - OSS editor <see cref="VsCodeUserDataDiscovery"/> identifies.
///
/// <para><b>Found by one named path, never by walking global storage.</b> An extension's storage
/// folder holds what it saved, and the largest things in there are usually conversation transcripts,
/// so nothing is recognised by its size or by a search. A folder qualifies only where
/// <see cref="CacheUnderExtension"/> is a directory inside it. Cline creates that path by giving
/// <c>puppeteer-chromium-resolver</c> a <c>puppeteer</c> folder in its storage, and the extensions
/// built from Cline's source keep the same layout, so one name reaches the family and nothing
/// beside it.</para>
///
/// <para>Separate from the provider because it answers "where are these caches?", and the provider
/// answers "what inside one may go?". The second is <see cref="PuppeteerCacheSurvey"/>'s question,
/// asked of a root this has found.</para>
/// </summary>
internal sealed class VsCodeExtensionBrowserDiscovery(VsCodeUserDataDiscovery editors)
{
    /// <summary>Where an editor keeps every extension's storage folder, below its user-data folder.</summary>
    public static readonly string GlobalStorage = Path.Combine("User", "globalStorage");

    /// <summary>
    /// The cache root below an extension's storage folder. Its last name is the marker, and it is
    /// self-identifying: <c>puppeteer-chromium-resolver</c> creates it to hold the builds it downloads,
    /// in <c>@puppeteer/browsers</c>' layout of one folder per browser and one per build inside it.
    /// </summary>
    public static readonly string CacheUnderExtension = Path.Combine("puppeteer", ".chromium-browser-snapshots");

    /// <summary>
    /// Every cache, each with its survey taken. A link on the way down is named and never followed,
    /// because a path built from constants has passed through no filter that separates links (see
    /// <see cref="DerivedPath"/>).
    ///
    /// <para>A link that is itself one of the extension folders is neither followed nor named, on
    /// <see cref="VsCodeUserDataDiscovery.Discover"/>'s reasoning: this walk chooses which folders to
    /// look in, and what such a link points at was never identified as anything.</para>
    /// </summary>
    public ExtensionBrowserSearch Search(CancellationToken ct)
    {
        var caches = new List<ExtensionBrowserCache>();
        var declined = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var unreadable = false;

        var found = editors.Discover(ct);

        // An application-data root that will not be listed hides every editor in it, which is not
        // the same as there being none.
        foreach (var root in editors.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(root));
            unreadable = true;
        }

        // Whether the walk may carry on into the directory, saying why where it may not. A refusal is
        // not evidence that the directory is the one named, so it is never read as present.
        bool Reached(string from, string directory)
        {
            if (DerivedPath.FirstObstacleBetween(from, directory) is { } obstacle)
            {
                if (obstacle.IsLink)
                {
                    notes.Add(CacheLevelWalk.Note(obstacle.Path));
                    declined.Add((obstacle.Path, CacheLevelWalk.LinkReason));
                }
                else
                {
                    Unreached(obstacle.Path);
                }

                return false;
            }

            switch (LongPath.ProbeDirectory(directory))
            {
                case PathPresence.Present:
                    return true;

                case PathPresence.Refused:
                    Unreached(directory);
                    break;
            }

            return false;
        }

        void Unreached(string path)
        {
            notes.Add(UnreadableRoot.UnreachedNote(path));
            declined.Add((path, UnreadableRoot.UnreachedReason));
            unreadable = true;
        }

        foreach (var editor in found)
        {
            ct.ThrowIfCancellationRequested();

            var storage = Path.Combine(editor.Path, GlobalStorage);

            if (!Reached(editor.Path, storage))
            {
                continue;
            }

            var scan = ChildDirectories.Under(storage);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(storage));
                unreadable = true;
                continue;
            }

            foreach (var extension in scan.Directories)
            {
                ct.ThrowIfCancellationRequested();

                var folder = LongPath.Display(extension.FullName);
                var root = Path.Combine(folder, CacheUnderExtension);

                if (Reached(folder, root))
                {
                    caches.Add(new ExtensionBrowserCache(
                        editor, extension.Name, folder, root, PuppeteerCacheSurvey.Of(root, ct)));
                }
            }
        }

        return new ExtensionBrowserSearch(caches, declined, notes, unreadable);
    }
}
