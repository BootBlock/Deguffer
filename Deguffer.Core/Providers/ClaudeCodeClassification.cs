using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What every kind of Claude Code leftover is judged against, gathered once per planning pass.</summary>
/// <param name="Home">Claude Code's folder, already resolved and checked to be a real directory.</param>
/// <param name="Projects">Which sessions still have a transcript.</param>
/// <param name="Sessions">Which sessions are running.</param>
/// <param name="RecentSinceUtc">
/// Anything written at or after this instant is held back. Fixed once, for the reason
/// <see cref="MinimumAge"/> is an instant rather than a duration: the preview and the clean must agree.
/// </param>
internal sealed record ClaudeCodeEvidence(
    string Home,
    ClaudeCodeProjects Projects,
    ClaudeCodeSessionList Sessions,
    DateTime RecentSinceUtc);

/// <summary>What Deguffer decided about one kind of thing Claude Code leaves behind.</summary>
/// <param name="Folders">
/// The folders this kind was classified in, each of which becomes a §5.2 declaration for Explore.
/// </param>
/// <param name="Targets">What may be removed.</param>
/// <param name="Survivors">
/// What stays and must be shown to have stayed, each with the reason the user is given.
/// </param>
/// <param name="Recent">
/// What would have been offered and was held back for having been written recently. Asserted as well,
/// and counted apart because the row must not read as clear while any of it is on the disk.
/// </param>
/// <param name="Notes">What the user is told about this kind.</param>
/// <param name="Unreadable">Whether a folder refused to be listed.</param>
/// <param name="Refused">
/// Whether something recognised was left alone because Deguffer could not show it was finished with —
/// its process is running, or nothing could establish whether it is. Such a row's zero is not the
/// whole story. See <see cref="CleanupPlan.WasNotExamined"/>.
/// </param>
internal sealed record ClaudeCodeClassification(
    IReadOnlyList<string> Folders,
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Survivors,
    IReadOnlyList<(string Path, string Reason)> Recent,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable,
    bool Refused);

/// <summary>
/// Gathers one <see cref="ClaudeCodeClassification"/>.
///
/// <para>Shared because every kind carries the same safety facts, and a hand-written copy per kind is
/// where one of them goes missing: a folder that is a link is named and never listed through, a folder
/// that refused to be listed says so rather than reading as empty, and everything left alone is
/// asserted rather than merely omitted. The one exception is what another provider may remove, and
/// each kind that has one names it where it is decided.</para>
/// </summary>
internal sealed class ClaudeCodeClassificationBuilder
{
    private readonly List<string> _folders = [];
    private readonly List<DeletionTarget> _targets = [];
    private readonly List<(string Path, string Reason)> _survivors = [];
    private readonly List<(string Path, string Reason)> _recent = [];
    private readonly List<PlanNote> _notes = [];

    private bool _unreadable;
    private bool _refused;

    /// <summary>
    /// The reason a recently written item is left alone. A session can run for days, and a version of
    /// Claude Code that predates its list of running sessions is invisible to that list, so time is
    /// the second check behind it.
    /// </summary>
    public static string RecentReason =>
        $"Claude Code wrote this in the last {ClaudeCodeSessionRegistry.RecentWindow.TotalDays:0} days, "
        + "and a session that is still running may be using it.";

    public const string UnrecognisedReason =
        "Not something Deguffer recognises in Claude Code's folder, so it is left alone.";

    public const string UnlistedReason =
        "Deguffer could not list what is in this folder, so nothing in it was examined and it is left alone.";

    public const string UndatedReason =
        "Deguffer could not tell when Claude Code last wrote to this, so it is left alone.";

    /// <summary>
    /// Start on one folder: name it for the declaration, assert it, and list it. Null where there is
    /// nothing to classify, because the folder is absent, is a link, or refused to be listed — and in
    /// the last two cases that has already been said.
    /// </summary>
    public IReadOnlyList<FileSystemInfo>? Open(string folder, string reason)
    {
        switch (LongPath.ProbeDirectory(folder))
        {
            // Windows would not say whether the folder is there. Nothing in it was classified, and
            // an unqualified null would let the plan present that as a folder holding nothing.
            //
            // No survivor, unlike Unlisted below. That one was reached, so §5.6 can measure it
            // before the run and the assertion can fail. This one cannot be measured, so it would
            // record itself as "nothing to preserve" and pass over whatever happened to the folder —
            // and an assertion nobody can fail reads as one that held. DeclaredLocations.Unreachable
            // keeps the same rule.
            case PathPresence.Refused:
                Unreached(folder);
                return null;

            case PathPresence.Absent:
                return null;
        }

        if (LongPath.IsReparsePoint(folder))
        {
            Link(folder);
            return null;
        }

        _folders.Add(folder);

        if (FolderEntries.Of(folder) is not { } entries)
        {
            Unlisted(folder, reason);
            return null;
        }

        Keep(folder, reason);
        return entries;
    }

    /// <summary>Name a folder for the declaration without listing it, for one listed elsewhere.</summary>
    public void Folder(string folder) => _folders.Add(folder);

    public void Offer(DeletionTarget target) => _targets.Add(target);

    /// <summary>Left alone by rule, and asserted to survive.</summary>
    public void Keep(string path, string reason) => _survivors.Add((path, reason));

    /// <summary>Recognised, left alone because it could not be shown to be finished with, and asserted.</summary>
    public void Refuse(string path, string reason)
    {
        Keep(path, reason);
        _refused = true;
    }

    /// <summary>Recognised and finished with by every other test, and written too recently to offer.</summary>
    public void HoldRecent(string path) => _recent.Add((path, RecentReason));

    /// <summary>
    /// Offer a folder that has passed every other test, unless something wrote to it recently.
    ///
    /// <para><b>Dated by the folder, never by the files deep inside it.</b> <see cref="DirectoryAge"/>
    /// reads the newest of the folder's own timestamp and its immediate entries', and NTFS moves the
    /// folder's own timestamp whenever an entry is added or removed. An old date on a file inside can
    /// therefore never make a folder in use read as old. That matters most beside Claude Code's rewind
    /// snapshots, whose files were measured carrying their source file's date, months older than the
    /// folder holding them.</para>
    ///
    /// <para>A folder that would not be listed has no age and is refused: its own timestamp alone is
    /// the half that reads older than the truth.</para>
    /// </summary>
    /// <param name="recentSinceUtc">Anything written at or after this instant is held back.</param>
    /// <param name="isLeftover">Whether the folder is a leftover. See <see cref="DeleteStep.IsLeftover"/>.</param>
    public void OfferFolderOnceOldEnough(
        string path,
        string reason,
        DateTime recentSinceUtc,
        bool isLeftover,
        CancellationToken ct)
    {
        switch (DirectoryAge.Of(path, ct))
        {
            case null:
                Refuse(path, UndatedReason);
                break;

            case DateTime written when written >= recentSinceUtc:
                HoldRecent(path);
                break;

            case DateTime written:
                Offer(new DeletionTarget(path, reason, written, IsLeftover: isLeftover));
                break;
        }
    }

    /// <summary>A link where something recognisable was expected: named, asserted, never followed.</summary>
    public void Link(string path)
    {
        Refuse(path, CacheLevelWalk.LinkReason);
        _notes.Add(CacheLevelWalk.Note(path));
    }

    /// <summary>
    /// A folder that refused to be listed, so nothing inside it was examined. Asserted like anything
    /// else left alone: a folder nobody could list is still a folder that must be there afterwards.
    /// </summary>
    public void Unlisted(string path, string reason)
    {
        Keep(path, reason);
        _unreadable = true;
        _notes.Add(UnreadableRoot.Note(path));
    }

    /// <summary>
    /// Record a folder Windows would not describe at all. <see cref="Unlisted"/>'s sentence would
    /// assert the folder is there, which nothing here established.
    /// </summary>
    private void Unreached(string path)
    {
        _unreadable = true;
        _notes.Add(UnreadableRoot.UnreachedNote(path));
    }

    public void Note(PlanNoteSeverity severity, string message) => _notes.Add(new PlanNote(severity, message));

    public ClaudeCodeClassification Build() =>
        new(_folders, _targets, _survivors, _recent, _notes, _unreadable, _refused);

    /// <summary>"1 file" or "3 files", for a sentence that has to read correctly on a machine with one.</summary>
    public static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
