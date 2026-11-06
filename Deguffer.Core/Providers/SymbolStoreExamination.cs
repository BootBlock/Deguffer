using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>One downloaded file's folder in a store, which a plan may offer whole.</summary>
/// <param name="Path">The entry, named for the file it holds every copy of.</param>
/// <param name="Parent">The store, or the two-letter folder of a two-level store, that it is in.</param>
/// <param name="Store">The store it is in.</param>
/// <param name="LastWritten">
/// When it was last written, by <see cref="DirectoryAge"/>'s rule: the newest of its own time, from its
/// parent's listing, and its index folders', from its own. Both are the listings that recognised it, so
/// dating it lists nothing again.
/// </param>
internal sealed record SymbolStoreEntry(string Path, string Parent, SymbolStore Store, DateTime LastWritten);

/// <summary>
/// What one look into each of a row's stores found: every entry a plan may offer, and everything that
/// must survive. The one pass the plan and Explore's refusals are both built from, so the two cannot
/// disagree about what is a downloaded copy.
///
/// <para><b>An entry is recognised by its whole shape.</b> Its name has an extension, every folder in it
/// is an index, and every index holds only the file the entry is named for, the compressed form of it or
/// a pointer to it. In a two-level store, the entry must also start with the two letters of the folder
/// it is in. Anything else, a link, a file at the root or a folder of any other shape, survives and is
/// asserted to (§5.2, §5.6).</para>
///
/// <para><b>A store SymStore built is left alone whole.</b> Its <c>000Admin</c> folder says so, and every
/// symbol in it may be the only copy.</para>
///
/// <para><b>A store inside another is that store's.</b> Where one folder a setting names is inside
/// another, the outer store neither offers nor asserts the inner one, because the inner store's own
/// row empties it.</para>
///
/// <para><b>Each store is read in two steps.</b> Its own listing, and each two-letter folder's, are read
/// in order, which names every folder that may be an entry. Each of those is then looked inside on
/// several threads at once (<see cref="ListWorkers"/>), because a debugger's store holds one entry per
/// module it has loaded symbols for, often thousands, and each look waits on the disk. What both steps
/// found is then recorded in listing order, so the entries, the survivors and the notes read the same
/// however the looks finished.</para>
/// </summary>
internal sealed class SymbolStoreExamination
{
    /// <summary>
    /// How many entries are looked inside at once. Each look lists the entry and then each index folder in
    /// it, small listings that wait on the disk rather than the processor, and the planner already plans
    /// several rows at once.
    /// </summary>
    private const int EntriesAtOnce = 8;

    private const string StoreReason = "The symbol store itself. Only the downloaded copies inside it are removed.";

    private const string MarkerReason = "A marker the symbol server program keeps in the store, so the store keeps working.";

    private const string LevelReason = "A folder of the two-level store layout. Only the downloaded copies inside it are removed.";

    private const string UnrecognisedReason = "Not shaped like a copy a symbol server program downloaded, so it is left alone.";

    public const string RefusedReason =
        "SymStore built this store, so it is somebody's own symbol store rather than a cache, and every symbol in it "
        + "may be the only copy. Deguffer never removes anything from it.";

    private readonly IReadOnlyList<(string Path, ReachedFolder Reached)> _everyStore;
    private readonly IVolumeInventory _volumes;
    private readonly List<(string Path, string Reason)> _survivors = [];

    // Asked once per level and per unrecognised entry, of a store that may hold tens of thousands.
    private readonly HashSet<string> _survived = new(StringComparer.OrdinalIgnoreCase);

    private SymbolStoreExamination(IReadOnlyList<SymbolStore> everyStore, IVolumeInventory volumes)
    {
        _volumes = volumes;
        _everyStore = [.. everyStore.Select(store => (store.Path, ReachedFolder.At(store.Path, volumes)))];
    }

    /// <summary>The stores that are there, whether or not anything in them is offered.</summary>
    public List<SymbolStore> Present { get; } = [];

    public List<SymbolStoreEntry> Entries { get; } = [];

    /// <summary>The two-letter folders of each two-level store, by the store they are in.</summary>
    public List<(string Path, SymbolStore Store)> Levels { get; } = [];

    /// <summary>Stores SymStore built, which nothing is ever removed from.</summary>
    public List<SymbolStore> Refused { get; } = [];

    /// <summary>Everything that must survive, each once, in the order the listings found it.</summary>
    public IReadOnlyList<(string Path, string Reason)> Survivors => _survivors;

    public List<PlanNote> Notes { get; } = [];

    /// <summary>Links left alone where a store or an entry was expected.</summary>
    public List<string> Declined { get; } = [];

    public bool Unreadable { get; private set; }

    public static string HeldReason(IReadOnlyList<string> running) =>
        $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running and may be downloading into this "
        + "store, so it is left alone. Close the debugger, then scan again.";

    /// <param name="stores">The row's own stores.</param>
    /// <param name="everyStore">Every store either row examines, so a store inside another is left to its own row.</param>
    public static SymbolStoreExamination Of(
        IReadOnlyList<SymbolStore> stores,
        IReadOnlyList<SymbolStore> everyStore,
        IVolumeInventory volumes,
        CancellationToken ct)
    {
        var examination = new SymbolStoreExamination(everyStore, volumes);

        foreach (var store in stores)
        {
            ct.ThrowIfCancellationRequested();
            examination.Collect(store, ct);
        }

        return examination;
    }

    private void Collect(SymbolStore store, CancellationToken ct)
    {
        switch (LongPath.ProbeDirectory(store.Path))
        {
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                Notes.Add(UnreadableRoot.UnreachedNote(store.Path));
                Survive(store.Path, UnreadableRoot.UnreachedReason);
                Unreadable = true;
                return;
        }

        // A setting's own folder is taken as the setting gives it, as a debugger opens it. A link there is
        // declined all the same, because what it leads to was never named by anything.
        if (LongPath.IsReparsePoint(store.Path))
        {
            DeclineLink(store.Path);
            return;
        }

        if (FolderEntries.Of(store.Path) is not { } entries)
        {
            Unlisted(store.Path);
            return;
        }

        Present.Add(store);

        if (entries.Any(entry => entry is DirectoryInfo && entry.Name.Equals(SymbolStoreLayout.AdministrationFolder, StringComparison.OrdinalIgnoreCase)))
        {
            Refused.Add(store);
            Survive(store.Path, RefusedReason);
            Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{store.Path}' alone: {RefusedReason}"));
            return;
        }

        Survive(store.Path, StoreReason);

        var reached = ReachedFolder.At(store.Path, _volumes);
        var inner = _everyStore
            .Where(other => !other.Path.Equals(store.Path, StringComparison.OrdinalIgnoreCase) && reached.Holds(other.Reached))
            .Select(other => other.Reached)
            .ToList();

        var twoLevel = entries.Any(entry => entry is FileInfo && entry.Name.Equals(SymbolStoreLayout.TwoLevelFile, StringComparison.OrdinalIgnoreCase));
        var walk = new StoreWalk();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (IsAnotherStore(path, inner))
            {
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                walk.Saw(Seen.Link, path);
            }
            else if (entry is not DirectoryInfo)
            {
                walk.Saw(Seen.Survivor, path, SymbolStoreLayout.RootMarkers.Contains(entry.Name) ? MarkerReason : UnrecognisedReason);
            }
            else if (!twoLevel)
            {
                walk.Consider(entry, path, store.Path, level: null);
            }
            else if (entry.Name.Length == 2)
            {
                WalkLevel(walk, path, inner, ct);
            }
            else
            {
                walk.Saw(Seen.Survivor, path, UnrecognisedReason);
            }
        }

        var written = new DateTime?[walk.Candidates.Count];
        ListWorkers.Run(written.Length, EntriesAtOnce, index => written[index] = Recognise(walk.Candidates[index]), ct);

        Record(walk, written, store);
    }

    /// <summary>One two-letter folder of a two-level store, and each entry in it.</summary>
    private void WalkLevel(StoreWalk walk, string level, IReadOnlyList<ReachedFolder> inner, CancellationToken ct)
    {
        if (FolderEntries.Of(level) is not { } entries)
        {
            walk.Saw(Seen.Unlisted, level);
            return;
        }

        walk.Saw(Seen.Level, level);

        var letters = Path.GetFileName(level);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (IsAnotherStore(path, inner))
            {
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                walk.Saw(Seen.Link, path);
            }
            else if (entry is not DirectoryInfo)
            {
                walk.Saw(Seen.Survivor, path, UnrecognisedReason);
            }
            else
            {
                walk.Consider(entry, path, level, letters);
            }
        }
    }

    /// <summary>
    /// What one store's walk and the looks inside its candidates found, in the order the listings found
    /// it. <paramref name="written"/> holds each candidate's answer from <see cref="Recognise"/>.
    /// </summary>
    private void Record(StoreWalk walk, DateTime?[] written, SymbolStore store)
    {
        var next = 0;

        foreach (var (what, path, reason) in walk.Sightings)
        {
            switch (what)
            {
                case Seen.Survivor:
                    Survive(path, reason!);
                    break;

                case Seen.Link:
                    DeclineLink(path);
                    break;

                case Seen.Unlisted:
                    Unlisted(path);
                    break;

                case Seen.Level:
                    Levels.Add((path, store));
                    Survive(path, LevelReason);
                    break;

                case Seen.Candidate:
                    var candidate = walk.Candidates[next];

                    if (written[next++] is { } lastWritten)
                    {
                        Entries.Add(new SymbolStoreEntry(path, candidate.Parent, store, lastWritten));
                    }
                    else
                    {
                        Survive(path, UnrecognisedReason);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// When <paramref name="candidate"/> was last written, where every folder in it is an index holding
    /// only copies of the file it is named for, and null where it is not an entry. Nothing here is
    /// followed through a link, and a folder that will not be listed is not one anybody has seen inside.
    /// </summary>
    private static DateTime? Recognise(Candidate candidate)
    {
        if (FolderEntries.Of(candidate.Path) is not { } indexes)
        {
            return null;
        }

        foreach (var index in indexes)
        {
            if (index is not DirectoryInfo
                || index.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !SymbolStoreLayout.IsIndex(index.Name)
                || FolderEntries.Of(LongPath.Display(index.FullName)) is not { } files
                || !files.All(file => file is FileInfo
                    && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && SymbolStoreLayout.IsIndexedFile(candidate.Name, file.Name)))
            {
                return null;
            }
        }

        return DirectoryAge.Of(candidate.Written, indexes.Select(MinimumAge.NewestFileTimeOf));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of the stores <paramref name="inner"/> names, all of which are
    /// inside the store being read. Asked only where there is one, because following a path asks the
    /// machine where its volume is mounted, and a store holds thousands of entries.
    /// </summary>
    private bool IsAnotherStore(string path, IReadOnlyList<ReachedFolder> inner) =>
        inner.Count > 0 && inner.Any(ReachedFolder.At(path, _volumes).IsSameAs);

    private void Survive(string path, string reason)
    {
        if (_survived.Add(path))
        {
            _survivors.Add((path, reason));
        }
    }

    private void DeclineLink(string path)
    {
        Declined.Add(path);
        Survive(path, CacheLevelWalk.LinkReason);
        Notes.Add(CacheLevelWalk.Note(path));
    }

    private void Unlisted(string path)
    {
        Notes.Add(UnreadableRoot.Note(path));
        Survive(path, UnreadableRoot.UnreachedReason);
        Unreadable = true;
    }

    /// <summary>What a store's walk saw at one place in its listings.</summary>
    private enum Seen
    {
        Survivor,
        Link,
        Unlisted,
        Level,

        /// <summary>A folder named like an entry: the next of <see cref="StoreWalk.Candidates"/>.</summary>
        Candidate,
    }

    /// <summary>A folder named like an entry, to be looked inside.</summary>
    /// <param name="Written">
    /// The newer of its own creation and last-write times as a FILETIME, from its parent's listing.
    /// </param>
    private readonly record struct Candidate(string Path, string Name, string Parent, long Written);

    /// <summary>
    /// Everything one store's listings found, in their order, and the folders named like entries among
    /// them, which are looked inside once the walk has ended.
    /// </summary>
    private sealed class StoreWalk
    {
        public List<(Seen What, string Path, string? Reason)> Sightings { get; } = [];

        public List<Candidate> Candidates { get; } = [];

        public void Saw(Seen what, string path, string? reason = null) => Sightings.Add((what, path, reason));

        /// <summary>
        /// A folder in <paramref name="parent"/>: a candidate where its name is an entry's and, in a
        /// two-level store, starts with the two letters of <paramref name="level"/>.
        /// </summary>
        public void Consider(FileSystemInfo folder, string path, string parent, string? level)
        {
            if (SymbolStoreLayout.IsEntryName(folder.Name) && (level is null || SymbolStoreLayout.BelongsUnder(level, folder.Name)))
            {
                Candidates.Add(new Candidate(path, folder.Name, parent, MinimumAge.NewestFileTimeOf(folder)));
                Saw(Seen.Candidate, path);
            }
            else
            {
                Saw(Seen.Survivor, path, UnrecognisedReason);
            }
        }
    }
}
