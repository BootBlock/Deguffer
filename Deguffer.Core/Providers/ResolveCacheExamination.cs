using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One project's render cache a plan may offer.</summary>
/// <param name="Path">The project's folder inside <c>CacheClip</c>.</param>
/// <param name="Cache">The <c>CacheClip</c> folder it is in, which must survive.</param>
internal sealed record ResolveRenderFolder(string Path, string Cache);

/// <summary>One <c>CacheClip</c> folder, and the children of it that are render cache.</summary>
internal sealed record ResolveCacheFolder(string Path, IReadOnlySet<string> RenderFolderNames);

/// <summary>
/// What one look at every candidate folder found: the <c>CacheClip</c> folders, what in each is
/// render cache, and everything that must survive. The one pass the plan and Explore's refusals are
/// both built from, so the two can never disagree about what is Resolve's.
/// </summary>
internal sealed class ResolveCacheExamination
{
    public List<ResolveCacheFolder> Caches { get; } = [];

    public List<ResolveRenderFolder> RenderFolders { get; } = [];

    public List<(string Path, string Reason)> Survivors { get; } = [];

    /// <summary>
    /// Folders left alone that may hold render cache: a link, never followed, and a project folder
    /// holding something besides render files. A row that offers nothing must not read as clear
    /// while one is there.
    /// </summary>
    public List<string> Declined { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    public bool Unreadable { get; private set; }

    /// <summary>Whether any candidate folder gave this pass anything to say.</summary>
    public bool FoundNothing => Caches.Count == 0 && Declined.Count == 0 && !Unreadable;

    public static ResolveCacheExamination Of(IReadOnlyList<string> folders, CancellationToken ct)
    {
        var examination = new ResolveCacheExamination();

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            examination.Collect(folder, ct);
        }

        return examination;
    }

    /// <summary>
    /// One candidate folder: its <c>CacheClip</c>, if there is one, and each child of that classified.
    /// A <c>CacheClip</c> that is a link is declined, since what it points at was never classified.
    /// </summary>
    private void Collect(string folder, CancellationToken ct)
    {
        var cache = Path.Combine(folder, ResolveCacheLayout.CacheFolderName);

        switch (LongPath.ProbeDirectory(cache))
        {
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                Notes.Add(UnreadableRoot.UnreachedNote(cache));
                Survivors.Add((cache, UnreadableRoot.UnreachedReason));
                Unreadable = true;
                return;
        }

        if (LongPath.IsReparsePoint(cache))
        {
            DeclineLink(cache);
            return;
        }

        if (FolderEntries.Of(cache) is not { } entries)
        {
            Notes.Add(UnreadableRoot.Note(cache));
            Survivors.Add((cache, UnreadableRoot.UnreachedReason));
            Unreadable = true;
            return;
        }

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeclineLink(path);
            }
            else if (entry is not DirectoryInfo)
            {
                Survivors.Add((path, $"'{entry.Name}' sits in Resolve's cache folder and is not a project's render cache, so it is left alone."));
            }
            else if (ResolveCacheLayout.NeverCacheReason(entry.Name) is { } reason)
            {
                Survivors.Add((path, reason));
            }
            else
            {
                Classify(entry.Name, path, cache, recognised, ct);
            }
        }

        Caches.Add(new ResolveCacheFolder(cache, recognised));

        // Named whether or not anything here is offered, because Explore refuses what a provider names
        // as protected (§7.1), and the neighbours hold the user's only copies.
        Survivors.Add((cache, "Resolve's cache folder itself. Only the render cache inside it is removed."));
        Survivors.AddRange(ResolveCacheLayout.Neighbours
            .Select(neighbour => (Path: Path.Combine(folder, neighbour.Name), neighbour.Reason))
            .Where(neighbour => LongPath.ProbeEntry(neighbour.Path) is not PathPresence.Absent));
    }

    /// <summary>One project folder, offered only where it holds render files and nothing else.</summary>
    private void Classify(string name, string path, string cache, HashSet<string> recognised, CancellationToken ct)
    {
        switch (ResolveCacheLayout.Read(path, ct))
        {
            case RenderFolderReading.RenderCache:
                recognised.Add(name);
                RenderFolders.Add(new ResolveRenderFolder(path, cache));
                break;

            case RenderFolderReading.NoRenderFile:
                Survivors.Add((path, $"'{name}' holds no Resolve render file, so it was not recognised as render cache and is left alone."));
                break;

            case RenderFolderReading.Mixed:
                Declined.Add(path);
                Survivors.Add((path, $"'{name}' holds something other than Resolve render files, so it was not recognised as render cache and is left alone."));
                Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{path}' alone: it holds something other than Resolve's render files, and "
                    + "Deguffer removes a project's render cache only where that is all it holds."));
                break;

            case RenderFolderReading.Unreadable:
                Notes.Add(UnreadableRoot.Note(path));
                Survivors.Add((path, UnreadableRoot.UnreachedReason));
                Unreadable = true;
                break;
        }
    }

    private void DeclineLink(string path)
    {
        Declined.Add(path);
        Survivors.Add((path, CacheLevelWalk.LinkReason));
        Notes.Add(CacheLevelWalk.Note(path));
    }
}
