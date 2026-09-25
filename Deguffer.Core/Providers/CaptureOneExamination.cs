using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One <c>Cache</c> folder a plan may offer, before the live-tree check.</summary>
/// <param name="Cache">The folder itself.</param>
/// <param name="Owner">The catalog or session it belongs to, which must survive.</param>
/// <param name="Reason">What the step says it removes.</param>
internal sealed record CaptureOneCandidate(string Cache, string Owner, string Reason);

/// <summary>What one planning pass has found so far, across every catalog and session.</summary>
internal sealed class CaptureOneExamination
{
    public List<CaptureOneCandidate> Candidates { get; } = [];

    /// <summary>The files Capture One holds open while it has each catalog or session open.</summary>
    public Dictionary<string, IReadOnlyList<string>> LockFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, string Reason)> Declined { get; } = [];

    public List<(string Path, string Reason)> Survivors { get; } = [];

    public List<string> Disconnected { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    public bool Unreadable { get; set; }

    public string OwnerOf(string cache) =>
        Path.GetFileName(Candidates.First(c => c.Cache.Equals(cache, StringComparison.OrdinalIgnoreCase)).Owner);

    /// <summary>
    /// Whether <paramref name="folder"/> is there to examine. A folder on a drive that is not
    /// connected is recorded for one sentence; one Windows would not describe is a warning.
    /// </summary>
    public bool Reached(string folder)
    {
        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                Disconnected.Add(folder);
                return false;

            case PathPresence.Refused:
                Notes.Add(UnreadableRoot.UnreachedNote(folder));
                Unreadable = true;
                return false;
        }

        return true;
    }

    /// <summary>The entries of <paramref name="folder"/>, or null with a warning where it would not be listed.</summary>
    public IReadOnlyList<FileSystemInfo>? EntriesOf(string folder)
    {
        if (FolderEntries.Of(folder) is { } entries)
        {
            return entries;
        }

        Notes.Add(UnreadableRoot.Note(folder));
        Unreadable = true;
        return null;
    }

    /// <summary>
    /// The <c>Cache</c> folder among <paramref name="entries"/> where it holds something, or null.
    /// A link of that name is declined and named, since what it points at was never classified.
    /// </summary>
    public string? CacheIn(string folder, IReadOnlyList<FileSystemInfo> entries)
    {
        if (entries.FirstOrDefault(entry => entry is DirectoryInfo && CaptureOneLayout.IsCache(entry.Name))
            is not { } cache)
        {
            return null;
        }

        var path = LongPath.Display(cache.FullName);

        if (cache.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            DeclineLink(path);
            return null;
        }

        return DirectoryContent.IsPresent(path) ? path : null;
    }

    public void Decline(string path, string reason)
    {
        Declined.Add((path, reason));
        Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{path}' alone. {reason}"));
    }

    /// <summary>A link, in the wording every provider uses for one it will not follow.</summary>
    public void DeclineLink(string path)
    {
        Declined.Add((path, CacheLevelWalk.LinkReason));
        Notes.Add(CacheLevelWalk.Note(path));
    }
}
