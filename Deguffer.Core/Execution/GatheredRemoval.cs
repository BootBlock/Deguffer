using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What the removal of one directory found before it deleted anything: what stands at the path, and
/// for a directory, everything its walk gathered. <see cref="DirectoryRemover"/> deletes it.
///
/// <para><b>Apart from the deletion, so a removal that goes whole or not at all is judged on the walk
/// it then acts on.</b> Looked at in a walk of its own first, a tree was listed twice in a row, and a
/// store that arrived between the two walks was stepped over by a removal already judged to have
/// none. The evidence the judgement reads is here, beside what the deletion takes, so no tree is
/// walked again between the judgement and the deletion. See <see cref="WholeTreeLook.Of"/>.</para>
/// </summary>
/// <param name="Extended">The directory to remove, in the extended-length form §6.3 requires.</param>
/// <param name="Bounds">What the removal must leave behind, which the walk has already applied.</param>
internal abstract record GatheredRemoval(string Extended, RemovalBounds Bounds)
{
    /// <summary>Outlook data files the removal would step over, in extended form.</summary>
    public virtual IReadOnlyList<string> MailStores => [];

    /// <summary>Folders Windows would not let the removal list, in extended form.</summary>
    public virtual IReadOnlyList<string> Unlisted => [];

    /// <summary>Paths met as folders where something else now stands, in extended form.</summary>
    public virtual IReadOnlyList<string> NotFolders => [];

    /// <summary>
    /// Whether anything inside changed within the guard on recently changed files: a file or a link
    /// the removal holds back, or a folder, whose own timestamps say something was written in it or
    /// taken out of it.
    /// </summary>
    public virtual bool HoldsRecent => false;

    /// <summary>
    /// Gather every tree in <paramref name="paths"/> as its removal would, off the calling thread, and
    /// delete nothing. Each is then removed by <see cref="DirectoryRemover.DeleteAsync"/>, which walks
    /// no tree again.
    /// </summary>
    /// <exception cref="OperationCanceledException">The clean was cancelled before every tree was gathered.</exception>
    public static Task<IReadOnlyList<GatheredRemoval>> TakeAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        IFileSystem fs,
        TimeProvider time,
        CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync<IReadOnlyList<GatheredRemoval>>(
            () => [.. paths.Select(path => Take(path, keep, RemovalBounds.None, fs, time, ct))],
            ct);

    /// <summary>
    /// What stands at <paramref name="path"/>, and for a directory, everything its removal would act
    /// on. Nothing is deleted.
    /// </summary>
    /// <param name="time">The clock the walk of a directory is timed on. See <see cref="Tree.Walk"/>.</param>
    /// <exception cref="OperationCanceledException">The walk was cancelled.</exception>
    public static GatheredRemoval Take(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        TimeProvider time,
        CancellationToken ct)
    {
        var extended = LongPath.Extended(path);
        var presence = fs.ProbeDirectory(extended);

        // The directory probe answers Absent for a file, so absence is decided by the entry probe. A
        // file that has taken the directory's name since the preview is nothing this step planned to
        // remove, and reading it as the directory gone reported "Removed." for a path still occupied —
        // and let the directory an index belongs to go while a file stood where the index was.
        if (presence is PathPresence.Absent)
        {
            presence = fs.ProbeEntry(extended);

            if (presence is PathPresence.Present)
            {
                return new NotAFolder(extended, bounds);
            }
        }

        switch (presence)
        {
            case PathPresence.Absent:
                return new Gone(extended, bounds);

            case PathPresence.Refused:
                return new Refused(extended, bounds);
        }

        // The root is the one entry no enumeration classified, so it is the one place a link can
        // still be walked through. Enumerating a junction returns the target's children, which are
        // ordinary directories and files, so the walk would gather a tree nobody looked at and the
        // §5.6 negative — written against paths inside the profile — would pass. The link is removed
        // and nothing behind it, exactly as the walk does for a link it finds below.
        if (fs.IsReparsePoint(extended))
        {
            // The guard covers the root exactly as the walk covers a link below it, and for the same
            // reason: a junction carries its own timestamps, so one made an hour ago is an hour old
            // whatever it points at. Without this a cache directory somebody had just relocated with
            // mklink was removed under a plan promising nothing touched in the last eight hours
            // would be — silently, because a link's length is zero and the outcome reported success.
            //
            // Asked of the path rather than of an enumerated entry, because a root is the one thing
            // no enumeration classified. FileRemover re-asks the same question the same way.
            var kept = fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest);

            return new Link(extended, bounds, kept);
        }

        // Two passes: gather the tree first so progress is a real fraction rather than a guess,
        // then delete depth-first. Gathering also means a mid-run enumeration failure cannot
        // leave us deleting a partially-understood tree.
        var started = time.GetTimestamp();
        var inventory = RemovalWalk.Gather(extended, keep, bounds, fs, ct);
        var walk = new PassTiming(
            time.GetElapsedTime(started),
            inventory.Files.Count + inventory.Directories.Count + inventory.Links.Count);

        return new Tree(extended, bounds, inventory, walk);
    }

    /// <summary>Nothing stands at the path, so nothing inside it can be lost.</summary>
    public sealed record Gone(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds);

    /// <summary>
    /// Windows would not say whether the directory is there. Nothing is removed, and the path is named
    /// as a folder that would not be listed, because nobody can say what it holds.
    /// </summary>
    public sealed record Refused(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds)
    {
        public override IReadOnlyList<string> Unlisted => [Extended];
    }

    /// <summary>A file, or something else that is not a directory, has taken the directory's name.</summary>
    public sealed record NotAFolder(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds)
    {
        public override IReadOnlyList<string> NotFolders => [Extended];
    }

    /// <summary>
    /// The path is a junction or a symbolic link, which is removed as a link and never followed.
    /// </summary>
    /// <param name="IsKept">Whether the guard holds it back, by the link's own timestamps.</param>
    public sealed record Link(string Extended, RemovalBounds Bounds, bool IsKept) : GatheredRemoval(Extended, Bounds)
    {
        public override bool HoldsRecent => IsKept;
    }

    /// <summary>A directory, and everything its walk gathered.</summary>
    /// <param name="Walk">
    /// How long the walk took and how many entries it gathered, the first of the removal's passes. See
    /// <see cref="RemovalPasses"/>.
    /// </param>
    public sealed record Tree(string Extended, RemovalBounds Bounds, RemovalInventory Inventory, PassTiming Walk)
        : GatheredRemoval(Extended, Bounds)
    {
        public override IReadOnlyList<string> MailStores => Inventory.MailStores;

        public override IReadOnlyList<string> Unlisted => Inventory.Unlisted;

        public override IReadOnlyList<string> NotFolders => Inventory.NotFolders;

        public override bool HoldsRecent => Inventory.Kept > 0 || Inventory.RecentFolders > 0;
    }
}
