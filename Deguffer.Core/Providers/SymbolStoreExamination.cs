using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One downloaded file's folder in a store, which a plan may offer whole.</summary>
/// <param name="Path">The entry, named for the file it holds every copy of.</param>
/// <param name="Parent">The store, or the two-letter folder of a two-level store, that it is in.</param>
/// <param name="Store">The store it is in.</param>
internal sealed record SymbolStoreEntry(string Path, string Parent, SymbolStore Store);

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
/// </summary>
internal sealed class SymbolStoreExamination
{
    private const string StoreReason = "The symbol store itself. Only the downloaded copies inside it are removed.";

    private const string MarkerReason = "A marker the symbol server program keeps in the store, so the store keeps working.";

    private const string LevelReason = "A folder of the two-level store layout. Only the downloaded copies inside it are removed.";

    private const string UnrecognisedReason = "Not shaped like a copy a symbol server program downloaded, so it is left alone.";

    public const string RefusedReason =
        "SymStore built this store, so it is somebody's own symbol store rather than a cache, and every symbol in it "
        + "may be the only copy. Deguffer never removes anything from it.";

    private readonly IReadOnlyList<(string Path, ReachedFolder Reached)> _everyStore;
    private readonly IVolumeInventory _volumes;

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

    public List<(string Path, string Reason)> Survivors { get; } = [];

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
                DeclineLink(path);
            }
            else if (entry is not DirectoryInfo)
            {
                Survive(path, SymbolStoreLayout.RootMarkers.Contains(entry.Name) ? MarkerReason : UnrecognisedReason);
            }
            else if (!twoLevel)
            {
                Classify(path, entry.Name, store.Path, level: null, store);
            }
            else if (entry.Name.Length == 2)
            {
                CollectLevel(path, store, inner, ct);
            }
            else
            {
                Survive(path, UnrecognisedReason);
            }
        }
    }

    /// <summary>One two-letter folder of a two-level store, and each entry in it.</summary>
    private void CollectLevel(string level, SymbolStore store, IReadOnlyList<ReachedFolder> inner, CancellationToken ct)
    {
        if (FolderEntries.Of(level) is not { } entries)
        {
            Unlisted(level);
            return;
        }

        Levels.Add((level, store));
        Survive(level, LevelReason);

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
                DeclineLink(path);
            }
            else if (entry is not DirectoryInfo)
            {
                Survive(path, UnrecognisedReason);
            }
            else
            {
                Classify(path, entry.Name, level, Path.GetFileName(level), store);
            }
        }
    }

    private void Classify(string path, string name, string parent, string? level, SymbolStore store)
    {
        if (SymbolStoreLayout.IsEntryName(name)
            && (level is null || SymbolStoreLayout.BelongsUnder(level, name))
            && HoldsOnlyCopies(path, name))
        {
            Entries.Add(new SymbolStoreEntry(path, parent, store));
        }
        else
        {
            Survive(path, UnrecognisedReason);
        }
    }

    /// <summary>
    /// Whether every folder in <paramref name="entry"/> is an index holding only copies of
    /// <paramref name="name"/>. Nothing here is followed through a link, and a folder that will not be
    /// listed is not one anybody has seen inside.
    /// </summary>
    private static bool HoldsOnlyCopies(string entry, string name)
    {
        if (FolderEntries.Of(entry) is not { } indexes)
        {
            return false;
        }

        foreach (var index in indexes)
        {
            if (index is not DirectoryInfo
                || index.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !SymbolStoreLayout.IsIndex(index.Name)
                || FolderEntries.Of(LongPath.Display(index.FullName)) is not { } files
                || !files.All(file => file is FileInfo
                    && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && SymbolStoreLayout.IsIndexedFile(name, file.Name)))
            {
                return false;
            }
        }

        return true;
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
        if (!Survivors.Any(survivor => survivor.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Survivors.Add((path, reason));
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
}
