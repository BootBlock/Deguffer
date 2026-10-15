using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>What a removal of one tree would act on, gathered before anything is deleted.</summary>
/// <param name="Directories">
/// Every directory the walk reached, the root first, each after the directory it was found in.
/// </param>
/// <param name="Files">
/// Every file the guard and the bounds leave to the removal, as the listing read it: its length, and
/// the attributes that say whether its read-only bit has to be cleared before it can go.
/// </param>
/// <param name="Links">Junctions and symbolic links, which are removed as links and never followed.</param>
/// <param name="Kept">Files and links the guard on recently changed files held back.</param>
/// <param name="Spared">
/// Each entry the bounds held back, in the extended form the walk met it in. The walk did not descend
/// into one, so nothing in the removal acts on it: see <see cref="RemovalOutcome.LeftAlone"/>.
/// </param>
/// <param name="MailStores">
/// Outlook mail stores the walk met, in the extended form it met them in. Never among
/// <paramref name="Files"/>, so nothing hands one to a delete. See <see cref="MailStore"/>.
/// </param>
internal sealed record RemovalInventory(
    IReadOnlyList<GatheredDirectory> Directories,
    IReadOnlyList<WalkEntry> Files,
    IReadOnlyList<WalkEntry> Links,
    int Kept,
    IReadOnlyList<string> Spared,
    IReadOnlyList<string> MailStores);

/// <summary>A directory the walk reached, in the extended form it met it in.</summary>
/// <param name="Parent">
/// The index in <see cref="RemovalInventory.Directories"/> of the directory the walk found this one
/// in, or <see cref="NoParent"/> for the root. Recorded here, where the walk knows it, rather than
/// recovered from the path, which a volume root's trailing separator makes a second spelling of.
/// </param>
internal readonly record struct GatheredDirectory(string Path, int Parent)
{
    public const int NoParent = -1;
}

/// <summary>
/// The walk a removal makes before it deletes anything, shared with the check that asks what Windows
/// would still refuse.
///
/// <para><b>One walk for both, because the check has to measure what the removal would
/// attempt.</b> The same guard, the same spared entries and the same refusal to follow a link decide
/// both. Written twice, the rules would drift, and the check would then take bytes out of an
/// estimate that the removal never meant to take — or leave in bytes it will.</para>
///
/// <para>Both exclusions are applied here rather than in the deletion pass because this is where the
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
    /// <param name="extendedRoot">The directory to walk, in the extended-length form §6.3 requires.</param>
    public static RemovalInventory Gather(
        string extendedRoot,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        CancellationToken ct) =>
        Gather(extendedRoot, keep, bounds, fs, WalkTuning.Default, ct);

    /// <summary>The same walk with <paramref name="tuning"/>'s workers, which is how a test sets them.</summary>
    internal static RemovalInventory Gather(
        string extendedRoot,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        WalkTuning tuning,
        CancellationToken ct)
    {
        var root = new Folder();

        // Most removals hold nothing back, and only a bound needs an entry's full path before the
        // removal acts on it. Building one per file to look up in an empty set would be most of what
        // gathering a large tree allocates.
        var bounded = bounds.SparedPaths.Count > 0 || bounds.OwnedElsewherePaths.Count > 0;

        BoundedFileWalk.Visit(
            extendedRoot,
            root,
            tuning,
            FileNames.All,
            fs.ListEntries,
            (folder, contents, descend) => folder.Gather(contents, descend, keep, bounds, bounded),
            static _ => { },
            TimeProvider.System,
            ct);

        return root.Inventory();
    }

    /// <summary>
    /// One directory the walk reached, and what the walk found directly inside it. Written only by the
    /// worker that listed the directory, so the workers share nothing and take no lock.
    ///
    /// <para>Its path is the listing's own, set when the directory is listed rather than when it is
    /// found, so the folder and every file gathered from it share one string for the whole removal.
    /// </para>
    /// </summary>
    private sealed class Folder
    {
        private string? _path;
        private List<Folder>? _folders;
        private List<WalkEntry>? _files;
        private List<WalkEntry>? _links;
        private List<string>? _spared;
        private List<string>? _mailStores;
        private int _kept;

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
            // read to the end, as it did when a failed listing threw.
            if (contents.WasRefused)
            {
                return;
            }

            _listed = contents.Entries.Count + contents.ReparseFiles.Count;

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
                var folder = new Folder();
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
        /// Everything gathered at and below this folder, each folder after the one it was found in.
        /// Read once the walk is over, with an explicit stack for the reason the walk has one.
        /// </summary>
        public RemovalInventory Inventory()
        {
            var directories = new List<GatheredDirectory>();
            var files = new List<WalkEntry>();
            var links = new List<WalkEntry>();
            var spared = new List<string>();
            var mailStores = new List<string>();
            var kept = 0;

            var pending = new Stack<(Folder Folder, int Parent)>();
            pending.Push((this, GatheredDirectory.NoParent));

            while (pending.TryPop(out var next))
            {
                var folder = next.Folder;
                var index = directories.Count;
                directories.Add(new GatheredDirectory(folder.Path, next.Parent));

                files.AddRange(folder._files ?? []);
                links.AddRange(folder._links ?? []);
                spared.AddRange(folder._spared ?? []);
                mailStores.AddRange(folder._mailStores ?? []);
                kept += folder._kept;

                // Pushed last first, so the folders come out in the order the listing met them.
                for (var child = (folder._folders?.Count ?? 0) - 1; child >= 0; child--)
                {
                    pending.Push((folder._folders![child], index));
                }
            }

            return new RemovalInventory(directories, files, links, kept, spared, mailStores);
        }

        // The walk lists every folder it is asked to descend into before it returns, so one never
        // listed is a walk that broke that promise, and removing it by a guessed path is not an option.
        private string Path => _path ?? throw new InvalidOperationException("A folder of the removal was never listed.");
    }
}
