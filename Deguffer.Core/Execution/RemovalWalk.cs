using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The walk a removal makes of its tree, shared with the check that asks what Windows would still
/// refuse. A removal deletes inside each folder as soon as the walk reports the folder listed (see
/// <see cref="TreeRemoval"/>), and the check and a removal that goes whole or not at all gather the
/// whole tree first.
///
/// <para><b>One walk for both, because the check has to measure what the removal would
/// attempt.</b> The same guard, the same spared entries and the same refusal to follow a link decide
/// both. Written twice, the rules would drift, and the check would then take bytes out of an
/// estimate that the removal never meant to take — or leave in bytes it will.</para>
///
/// <para>Both exclusions are applied here rather than in the deletion because this is where the
/// evidence already is: the enumeration that classified the entry read its timestamp and its path,
/// and gathering is also where a file the removal must not touch stops being a candidate at all. An
/// entry filtered out here is never handed to a delete, so there is no second place holding the same
/// rule.</para>
///
/// <para><b>On the walk every scan takes.</b> A tree of a million files and a hundred thousand
/// folders is listed by several workers at once, with no recursion for a deep tree to exhaust the
/// stack with, and through <see cref="IFileSystem.ListEntries"/>, so its tests still see every
/// listing. Each folder's findings stay with the folder until the walk is over, and are put in order
/// only then, so the inventory is the same however many workers there were and however their work
/// interleaved.</para>
/// </summary>
internal static class RemovalWalk
{
    /// <summary>
    /// Walk the whole tree at <paramref name="extendedRoot"/>, deleting nothing, and hand back its
    /// folder, from which every folder the walk reached is read in <see cref="Folder.InWalkOrder"/>.
    /// </summary>
    /// <param name="extendedRoot">The directory to walk, in the extended-length form §6.3 requires.</param>
    /// <exception cref="OperationCanceledException">The walk was cancelled.</exception>
    public static Folder Gather(
        string extendedRoot,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        CancellationToken ct) =>
        Gather(extendedRoot, keep, bounds, fs, WalkTuning.Default, ct);

    /// <summary>The same walk with <paramref name="tuning"/>'s workers, which is how a test sets them.</summary>
    internal static Folder Gather(
        string extendedRoot,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        WalkTuning tuning,
        CancellationToken ct)
    {
        var root = new Folder(extendedRoot);

        Visit(root, keep, bounds, fs, tuning, static _ => { }, ct);

        return root;
    }

    /// <summary>
    /// Walk the tree at <paramref name="root"/>, telling <paramref name="listed"/> of each folder as soon
    /// as its own listing has been read to the end and classified, while the rest of the tree is still
    /// being listed. The folders below <paramref name="root"/> are made by
    /// <see cref="Folder.NewChild"/>, so a caller that needs more of each folder hands a root of its own
    /// kind.
    ///
    /// <para>A folder is reported once, by the worker that listed it, before any folder inside it is
    /// listed. What it reports is final: nothing the walk does afterwards adds to it.</para>
    /// </summary>
    /// <exception cref="OperationCanceledException">The walk was cancelled.</exception>
    internal static void Visit<TFolder>(
        TFolder root,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        WalkTuning tuning,
        Action<TFolder> listed,
        CancellationToken ct)
        where TFolder : Folder
    {
        // Most removals hold nothing back, and only a bound needs an entry's full path before the
        // removal acts on it. Building one per file to look up in an empty set would be most of what
        // gathering a large tree allocates.
        var bounded = bounds.IsBounded;

        BoundedFileWalk.Visit(
            root.FoundAs,
            (Folder)root,
            tuning,
            FileNames.All,
            fs.ListEntries,
            (folder, contents, descend) =>
            {
                folder.Gather(contents, descend, keep, bounds, bounded);
                listed((TFolder)folder);
            },
            static _ => { },
            TimeProvider.System,
            ct);
    }

    /// <summary>
    /// One directory the walk reached, and what the walk found directly inside it. Written only by the
    /// worker that listed the directory, so the workers share nothing and take no lock.
    ///
    /// <para>Its path is the listing's own, set when the directory is listed rather than when it is
    /// found, so the folder and every file gathered from it share one string for the whole removal.
    /// </para>
    /// </summary>
    /// <param name="foundAs">
    /// The path the folder was found at, in extended form. What it is called where a walk stopped before
    /// it was listed.
    /// </param>
    internal class Folder(string foundAs)
    {
        private string? _path;
        private List<Folder>? _folders;
        private List<WalkEntry>? _files;
        private List<WalkEntry>? _links;
        private List<string>? _spared;
        private List<string>? _userWork;
        private List<string>? _mailStores;
        private int _kept;
        private int _recentFolders;

        // How the listing of this folder was refused, which is the evidence a removal that goes whole
        // or not at all is judged on. See Refusal.
        private ListingRefusal _refused;

        // How many entries the listing held that could be files, which the file list is sized for
        // when the first file arrives: a list grown by doubling allocates twice what it ends up holding.
        private int _listed;

        public void Gather(
            DirectoryContents contents,
            Action<WalkEntry, Folder> descend,
            MinimumAge keep,
            RemovalBounds bounds,
            bool bounded)
        {
            _path = contents.Directory;

            // Unreadable, or gone: nothing to gather, and §5.3 says skip rather than fail. What a
            // listing read before it failed is dropped too, so a removal acts only inside a folder it
            // read to the end, as it did when a failed listing threw. A refusal is still recorded,
            // because a removal that goes whole cannot step over a folder it could not read.
            if (contents.WasRefused)
            {
                if (contents.Presence is PathPresence.Refused)
                {
                    _refused = contents.End.Refusal;
                }

                return;
            }

            _listed = contents.Entries.Length + contents.ReparseFiles.Length;

            foreach (var entry in contents.Entries)
            {
                Classify(entry, descend, keep, bounds, bounded);
            }

            foreach (var entry in contents.Links)
            {
                Classify(entry, descend, keep, bounds, bounded);
            }

            foreach (var entry in contents.ReparseFiles)
            {
                Classify(entry, descend, keep, bounds, bounded);
            }
        }

        private void Classify(
            WalkEntry entry,
            Action<WalkEntry, Folder> descend,
            MinimumAge keep,
            RemovalBounds bounds,
            bool bounded)
        {
            var path = bounded ? entry.FullName : null;

            // First, because it is the reason that outlasts the others: an unsaved sketch an editor
            // has open is spared while the editor runs and is still the user's work once it closes.
            if (path is not null && bounds.UserWorkPaths.Contains(path))
            {
                (_userWork ??= []).Add(path);
                return;
            }

            // Asked before the link branch below, because a spared entry is spared whatever it
            // turns out to be. A scratch directory a running program was handed as a junction is
            // still that program's, and removing the link is still taking it away.
            if (path is not null && bounds.SparedPaths.Contains(path))
            {
                (_spared ??= []).Add(path);
                return;
            }

            // Another row's, so it is neither this removal's subject nor something it held back.
            if (path is not null && bounds.OwnedElsewherePaths.Contains(path))
            {
                return;
            }

            // §9, asked before the link branch and before the guard. Before the link branch, because the
            // mark Windows puts on a link is also on files that are not links — a OneDrive placeholder, a
            // deduplicated file — and deleting one of those deletes its content. Nothing here reads which
            // kind of mark a file carries, so a file named like a store is left whatever it carries. Before
            // the guard, because the rule is unconditional and the guard is a preference, so a store both
            // would keep is reported as the store it is. Its folders stay standing by the rule every kept
            // file relies on — a folder still holding something cannot be removed.
            if (entry.IsMailStore)
            {
                (_mailStores ??= []).Add(path ?? entry.FullName);
                return;
            }

            if (entry.IsReparsePoint)
            {
                // The guard applies to a link exactly as it applies to a file.
                //
                // A link carries its own timestamps, so a junction made a minute ago is a minute
                // old whatever it points at. Without this, a plan promising "nothing touched in the
                // last seven days is offered" removed one anyway — silently, because a link's
                // length is zero and every scanner skips it, so no figure moved and neither count
                // did either.
                if (keep.Protects(entry.NewestFileTime))
                {
                    _kept++;
                    return;
                }

                // Never followed: deletion would escape the target tree. The removal takes the link
                // itself and stops there.
                (_links ??= []).Add(entry);
                return;
            }

            if (entry.IsDirectory)
            {
                if (keep.Protects(entry.NewestFileTime))
                {
                    _recentFolders++;
                }

                var folder = NewChild(entry.FullName);
                (_folders ??= []).Add(folder);
                descend(entry, folder);
            }
            else if (keep.Protects(entry.NewestFileTime))
            {
                _kept++;
            }
            else
            {
                (_files ??= new List<WalkEntry>(_listed)).Add(entry);
            }
        }

        /// <summary>
        /// The path the folder was found at, in extended form, or the listing's own once it is listed.
        /// </summary>
        public string Path => _path ?? foundAs;

        /// <summary>The path the folder was found at, in extended form.</summary>
        public string FoundAs => foundAs;

        /// <summary>
        /// Every file in it the guard and the bounds leave to the removal, as the listing read it: its
        /// length, and the attributes that say whether its read-only bit has to be cleared before it
        /// can go.
        /// </summary>
        public IReadOnlyList<WalkEntry> Files => _files ?? [];

        /// <summary>Junctions and symbolic links in it, which are removed as links and never followed.</summary>
        public IReadOnlyList<WalkEntry> Links => _links ?? [];

        /// <summary>Files and links in it the guard on recently changed files held back.</summary>
        public int Kept => _kept;

        /// <summary>
        /// How many folders in it the guard would protect by their own timestamps. The removal takes
        /// each once it is empty all the same, so only a removal that goes whole or not at all reads it:
        /// a folder written in or taken from since the guard's moment may be one its owner is working in
        /// again.
        /// </summary>
        public int RecentFolders => _recentFolders;

        /// <summary>
        /// Entries in it the bounds held back because something is using them, in the extended form the
        /// walk met them in. The walk did not descend into one, so nothing in the removal acts on it: see
        /// <see cref="RemovalOutcome.LeftAlone"/>.
        /// </summary>
        public IReadOnlyList<string> Spared => _spared ?? [];

        /// <summary>Entries in it the bounds held back because they hold somebody's work.</summary>
        public IReadOnlyList<string> UserWork => _userWork ?? [];

        /// <summary>
        /// Outlook mail stores in it, in the extended form the walk met them in. Never among
        /// <see cref="Files"/>, so nothing hands one to a delete. See <see cref="MailStore"/>.
        /// </summary>
        public IReadOnlyList<string> MailStores => _mailStores ?? [];

        /// <summary>
        /// How Windows refused its listing, or <see cref="ListingRefusal.None"/>. A folder gone since its
        /// parent was listed is not refused, because nothing is left in it to lose.
        /// </summary>
        public ListingRefusal Refusal => _refused;

        /// <summary>
        /// Whether Windows refused to list it while a folder stood there, so nothing in it was seen. A
        /// file at its path is not this: nothing is inside a file to be lost.
        /// </summary>
        public bool IsUnlisted => _refused is ListingRefusal.Denied or ListingRefusal.Other;

        /// <summary>
        /// Let go of the files and links its listing found, once nothing will read them again: a removal
        /// of a large tree would otherwise hold every entry of it until the last folder had gone.
        /// </summary>
        public void ReleaseEntries()
        {
            _files = null;
            _links = null;
        }

        /// <summary>
        /// A folder found inside this one at <paramref name="path"/>, made as its listing finds it, and
        /// so before the walk lists it.
        /// </summary>
        protected virtual Folder NewChild(string path) => new(path);

        /// <summary>
        /// This folder and every folder below it, each after the folder it was found in, and the folders
        /// in one folder in the order its listing met them. Read once the walk is over, with an explicit
        /// stack for the reason the walk has one.
        /// </summary>
        public IReadOnlyList<Folder> InWalkOrder()
        {
            var order = new List<Folder>();

            var pending = new Stack<Folder>();
            pending.Push(this);

            while (pending.TryPop(out var next))
            {
                order.Add(next);

                // Pushed last first, so the folders come out in the order the listing met them.
                for (var child = (next._folders?.Count ?? 0) - 1; child >= 0; child--)
                {
                    pending.Push(next._folders![child]);
                }
            }

            return order;
        }
    }
}
