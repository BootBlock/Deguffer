using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One entry a RetroArch row offers.</summary>
internal sealed record RetroArchTarget(string Path, string Reason, TargetKind Kind, string Group);

/// <summary>
/// A folder a RetroArch row listed, the names in it the row recognised, and the folder it was reached
/// from, which is the program's folder where the folder is inside it.
/// </summary>
internal sealed record RetroArchFolderReading(string Top, string Path, IReadOnlySet<string> Recognised);

/// <summary>
/// What one pass over RetroArch found: what is offered, what survives and why, and what the plan has
/// to say. The finding of the installs starts one, and each row carries on from a copy of it.
///
/// <para>Every folder is reached by name from a setting, and the only folders listed are the ones a
/// row names, so nothing beside them is ever enumerated or classified. What survives is named instead,
/// which is how §5.6 asserts it.</para>
/// </summary>
internal sealed class RetroArchReading
{
    private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);

    public List<RetroArchTarget> Targets { get; } = [];

    public List<(string Path, string Reason)> Survivors { get; } = [];

    public List<string> Declined { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    public List<RetroArchFolderReading> Folders { get; } = [];

    public bool Unreadable { get; private set; }

    /// <summary>A reading that carries on from what <paramref name="found"/> already says.</summary>
    public static RetroArchReading From(RetroArchReading found)
    {
        var reading = new RetroArchReading { Unreadable = found.Unreadable };

        reading.Survivors.AddRange(found.Survivors);
        reading.Declined.AddRange(found.Declined);
        reading.Notes.AddRange(found.Notes);

        return reading;
    }

    /// <summary>
    /// The entries of <paramref name="folder"/>, or null where it is missing, a link, not reachable or
    /// not listable, each of the last three said so. Null as well for a folder this reading has already
    /// listed, so two installs sharing one folder offer its entries once.
    /// </summary>
    public IReadOnlyList<FileSystemInfo>? Entries(string folder)
    {
        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                return null;

            case PathPresence.Refused:
                Unreached(folder);
                return null;
        }

        if (LongPath.IsReparsePoint(folder))
        {
            DeclineLink(folder);
            return null;
        }

        if (!_listed.Add(folder))
        {
            return null;
        }

        if (FolderEntries.Of(folder) is not { } entries)
        {
            Notes.Add(UnreadableRoot.Note(folder));
            Survivors.Add((folder, UnreadableRoot.UnreachedReason));
            Unreadable = true;
            return null;
        }

        return entries;
    }

    public void Offer(string path, string reason, TargetKind kind, string group) =>
        Targets.Add(new RetroArchTarget(path, reason, kind, group));

    /// <summary>
    /// Name <paramref name="path"/> as a survivor, and tell the user where it is a folder they might
    /// have expected to go. A loose file is protected all the same, but a note for each would bury the
    /// ones that matter.
    /// </summary>
    public void Keep(string path, string reason, bool tell)
    {
        Survivors.Add((path, reason));

        if (tell)
        {
            Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{path}' alone: {reason}"));
        }
    }

    public void Unreached(string path)
    {
        Notes.Add(UnreadableRoot.UnreachedNote(path));
        Survivors.Add((path, UnreadableRoot.UnreachedReason));
        Unreadable = true;
    }

    public void Unread(string file, string consequence)
    {
        Notes.Add(new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not read '{file}', so {consequence}"));
        Survivors.Add((file, UnreadableRoot.UnreachedReason));
        Unreadable = true;
    }

    public void DeclineLink(string path)
    {
        Declined.Add(path);
        Survivors.Add((path, CacheLevelWalk.LinkReason));
        Notes.Add(CacheLevelWalk.Note(path));
    }
}
