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

        if (Probe(extended, keep, bounds, fs) is { } notATree)
        {
            return notATree;
        }

        // Walked to its end before anything is deleted, so the removal can be judged on everything it
        // would act on, and then deleted from what the walk gathered.
        var started = time.GetTimestamp();
        var root = new RemovalFolder(extended, parent: null);
        RemovalWalk.Visit(root, keep, bounds, fs, WalkTuning.Default, static _ => { }, ct);

        var took = time.GetElapsedTime(started);
        var entries = root.InWalkOrder().Sum(folder => 1L + folder.Files.Count + folder.Links.Count);

        return new Tree(extended, bounds, root, new PassTiming(took, entries));
    }

    /// <summary>
    /// What stands at <paramref name="extended"/> where it is not a directory to walk: nothing, a path
    /// Windows will not describe, a file, or a link. Null for a directory, which a removal walks.
    /// </summary>
    /// <param name="extended">The path, in the extended-length form §6.3 requires.</param>
    public static GatheredRemoval? Probe(string extended, MinimumAge keep, RemovalBounds bounds, IFileSystem fs)
    {
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

        return null;
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

    /// <summary>
    /// A directory, and everything its walk gathered, folder by folder. Deleted once, by
    /// <see cref="DirectoryRemover.DeleteAsync"/>: each folder counts down what is left in it as the
    /// deletion takes it.
    /// </summary>
    public sealed record Tree : GatheredRemoval
    {
        /// <param name="walk">
        /// How long the walk took and how many entries it gathered, the first of the removal's passes.
        /// See <see cref="RemovalPasses"/>.
        /// </param>
        public Tree(string extended, RemovalBounds bounds, RemovalFolder root, PassTiming walk)
            : base(extended, bounds)
        {
            Root = root;
            Walk = walk;

            var mailStores = new List<string>();
            var unlisted = new List<string>();
            var notFolders = new List<string>();

            foreach (var folder in root.InWalkOrder())
            {
                mailStores.AddRange(folder.MailStores);
                HoldsRecent |= folder.Kept > 0 || folder.RecentFolders > 0;

                switch (folder.Refusal)
                {
                    case ListingRefusal.None:
                        break;

                    case ListingRefusal.NotADirectory:
                        notFolders.Add(folder.Path);
                        break;

                    default:
                        unlisted.Add(folder.Path);
                        break;
                }
            }

            (MailStores, Unlisted, NotFolders) = (mailStores, unlisted, notFolders);
        }

        /// <summary>The directory's own folder, from which every folder below it is reached.</summary>
        public RemovalFolder Root { get; }

        /// <summary>How long the walk took, and how many entries it gathered.</summary>
        public PassTiming Walk { get; }

        public override IReadOnlyList<string> MailStores { get; }

        public override IReadOnlyList<string> Unlisted { get; }

        public override IReadOnlyList<string> NotFolders { get; }

        public override bool HoldsRecent { get; }
    }
}
