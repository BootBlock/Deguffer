using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One build folder <see cref="PuppeteerCacheLayout"/> recognised.</summary>
/// <param name="Path">The folder, in display form.</param>
/// <param name="Browser">Puppeteer's spelling of the browser folder it is in.</param>
/// <param name="Name">The folder's own name, <c>{platform}-{buildId}</c>.</param>
internal sealed record PuppeteerBuild(string Path, string Browser, string Name, string Platform, string BuildId);

/// <summary>
/// What the two levels of one Puppeteer cache hold: the builds it may offer, and everything it must
/// leave alone and say so about.
/// </summary>
/// <param name="BrowserFolders">The browser folders found, in display form.</param>
/// <param name="Declined">Every folder left alone at either level, and why, for §5.6.</param>
/// <param name="Unreadable">Whether the root or a browser folder would not be listed.</param>
internal sealed record PuppeteerCacheSurvey(
    IReadOnlyList<PuppeteerBuild> Builds,
    IReadOnlyList<string> BrowserFolders,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable)
{
    /// <param name="root">The cache root, already found on disk and not a link.</param>
    public static PuppeteerCacheSurvey Of(string root, CancellationToken ct)
    {
        var builds = new List<PuppeteerBuild>();
        var browserFolders = new List<string>();
        var declined = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var unreadable = false;

        // A listing right is separate from a traverse right, so a folder found by name may still
        // refuse. Without the note the plan has no steps and nothing said, which the shell renders
        // as "Already clear" about a folder nobody read.
        ChildDirectoryScan List(string folder)
        {
            var scan = ChildDirectories.Under(folder);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(folder));
                unreadable = true;
            }

            // A link is a child the user can see, so it is named rather than dropped, and it is never
            // followed: what it points at was never classified.
            foreach (var link in scan.Links)
            {
                var path = LongPath.Display(link.FullName);
                notes.Add(CacheLevelWalk.Note(path));
                declined.Add((path, CacheLevelWalk.LinkReason));
            }

            return scan;
        }

        void Decline(DirectoryInfo child, string why)
        {
            // §5.2: unrecognised means untouched, and the user is told rather than left to wonder
            // why the total is smaller than the folder.
            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{child.Name}' alone: {why}"));
            declined.Add((LongPath.Display(child.FullName), why));
        }

        foreach (var folder in List(root).Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (PuppeteerCacheLayout.Browser(folder.Name) is not { } browser)
            {
                Decline(folder, "not a browser Puppeteer downloads.");
                continue;
            }

            browserFolders.Add(LongPath.Display(folder.FullName));

            foreach (var child in List(folder.FullName).Directories)
            {
                ct.ThrowIfCancellationRequested();

                if (!PuppeteerCacheLayout.IsBuild(browser, child.Name))
                {
                    Decline(child, $"not a {browser} build Puppeteer downloaded.");
                    continue;
                }

                var (platform, buildId) = PuppeteerCacheLayout.Split(child.Name);

                // Enumeration runs in extended form; a plan always holds display paths, and I/O
                // re-extends at the point of use.
                builds.Add(new PuppeteerBuild(LongPath.Display(child.FullName), browser, child.Name, platform, buildId));
            }
        }

        return new PuppeteerCacheSurvey(builds, browserFolders, declined, notes, unreadable);
    }
}
