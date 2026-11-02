using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the two debugger symbol rows share: finding the stores, reading each one, holding a store back
/// while a debugger that writes to it runs, and telling Explore what in it may go. Each row states only
/// which stores are its own.
///
/// <para><b>Two rows because they are two tiers.</b> Microsoft says of a downstream store that "you can
/// delete this directory at any time to save disk space", and a store filled only from Microsoft's public
/// servers is downloaded again on demand, so it is Tier 1. A store filled from any other server may hold
/// symbols that server no longer keeps, so it is Tier 2, and so is a store whose servers Deguffer cannot
/// read, as WinDbg's and Visual Studio's are. A plan carries one tier, and
/// <see cref="SymbolStoreDiscovery"/> puts each store on exactly one row.</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> AgeStore, the only trimming tool Microsoft ships,
/// comes only with Debugging Tools for Windows, asks on standard input unless told not to, refuses to
/// run where NTFS does not record last access, and deletes the store itself once it is empty. No
/// debugger reports where its store is.</para>
///
/// <para><b>§5.2.</b> Only an entry shaped like a downloaded copy is removed, whole. The store, its
/// markers, the two-letter folders of a two-level store, and everything else in it survive, and §5.6
/// asserts each of them. A store SymStore built is refused whole. See
/// <see cref="SymbolStoreExamination"/>.</para>
///
/// <para><b>Nothing while a debugger that writes to the store runs (§5.3).</b> The clean asks again
/// before it removes each entry.</para>
/// </summary>
public abstract class DebuggerSymbolCacheProviderBase : CleanupProviderBase, ITemporaryFolderTenant
{
    private readonly SymbolStoreDiscovery _discovery;
    private readonly PassMemo<SymbolStoreExamination> _examination = new();

    private protected DebuggerSymbolCacheProviderBase(
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner,
        SymbolStoreDiscovery? discovery,
        ISystemDirectories? system,
        IVolumeInventory? volumes)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _discovery = discovery ?? new SymbolStoreDiscovery(Environment, system, Volumes);
    }

    public sealed override StepGrain Grain => StepGrain.Parts;

    /// <summary>The stores this row offers.</summary>
    private protected abstract SymbolStoreFeed Feed { get; }

    /// <summary>What the plan says where this row has no store at all.</summary>
    private protected abstract string NoStore { get; }

    private IReadOnlyList<SymbolStore> Stores => [.. _discovery.Find().Stores.Where(store => store.Feed == Feed)];

    private SymbolStoreExamination Examine(CancellationToken ct) =>
        _examination.Get(() => SymbolStoreExamination.Of(Stores, _discovery.Find().Stores, Volumes, ct));

    public override void InvalidateCaches()
    {
        _examination.Forget();
        _discovery.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a debugger is set to use a store of this row's, whether or not anything is in it yet.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(Stores.Count > 0);

    /// <summary>Each store, which is where the clean removes entries.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Stores.Select(store => CleanedPlace.Whole(store.Path))]);

    /// <summary>
    /// §5.2 as §7.1 reads it: each store and each two-letter folder recognises only the entries the plan
    /// would offer, and a store SymStore built, or one a running debugger writes to, recognises nothing.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);
        var offered = examination.Entries.ToLookup(entry => entry.Parent, entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase);
        var levels = examination.Levels.ToLookup(level => level.Store.Path, level => Path.GetFileName(level.Path), StringComparer.OrdinalIgnoreCase);
        List<ToolRoot> roots = [];

        foreach (var store in examination.Present)
        {
            if (examination.Refused.Contains(store))
            {
                roots.Add(new ToolRoot(store.Path, SymbolStoreExamination.RefusedReason, static _ => false));
            }
            else if (Inspector.FindRunning(store.Readers) is { Count: > 0 } running)
            {
                roots.Add(new ToolRoot(store.Path, SymbolStoreExamination.HeldReason(running), static _ => false));
            }
            else
            {
                var names = offered[store.Path].Concat(levels[store.Path]).ToHashSet(StringComparer.OrdinalIgnoreCase);

                roots.Add(ToolRoot.Folders(
                    store.Path,
                    "This is a debugger's symbol store. Deguffer removes only the copies of symbol files and programs "
                    + "it downloaded, and leaves the store and its markers.",
                    names.Contains));

                roots.AddRange(examination.Levels.Where(level => level.Store == store).Select(level => ToolRoot.Folders(
                    level.Path,
                    "This is a folder of a debugger's symbol store. Deguffer removes only the downloaded copies inside it.",
                    offered[level.Path].ToHashSet(StringComparer.OrdinalIgnoreCase).Contains)));
            }
        }

        return Task.FromResult<IReadOnlyList<ToolRoot>>(roots);
    }

    /// <summary>
    /// Each of this row's stores inside one of <paramref name="folders"/>, spelled from that folder, so the
    /// temporary-files row leaves the whole store to this row: Visual Studio keeps its own there by
    /// default. A store this row would not take today, because a debugger is running or SymStore built
    /// it, is claimed all the same.
    /// </summary>
    public Task<IReadOnlyList<string>> ClaimedEntriesAsync(IReadOnlyList<string> folders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var stores = Stores
            .Where(store => LongPath.ProbeDirectory(store.Path) is not PathPresence.Absent)
            .Select(store => Reach(store.Path))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(
        [
            .. from folder in folders
               let reached = Reach(folder)
               from store in stores
               let relative = reached.PathTo(store)
               where relative is not null and not "."
               select Path.Combine(folder, relative),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var finding = _discovery.Find();
        var stores = Stores;
        var notes = finding.Notes.Where(note => note.Feed == Feed).Select(note => note.Note).ToList();

        if (stores.Count == 0)
        {
            return Nothing(NoStore, notes, []);
        }

        var examination = Examine(ct);
        notes.AddRange(examination.Notes);

        notes.AddRange(examination.Present.Select(store => new PlanNote(
            PlanNoteSeverity.Information,
            $"A symbol store at '{store.Path}', named by {string.Join(" and ", store.NamedBy)}.")));

        var held = new List<(string Path, string Reason)>();
        var checks = new Dictionary<SymbolStore, RunningProcessCheck>();

        foreach (var store in examination.Present.Except(examination.Refused))
        {
            if (Inspector.FindRunning(store.Readers) is { Count: > 0 } running)
            {
                var reason = SymbolStoreExamination.HeldReason(running);
                held.Add((store.Path, reason));
                notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left '{store.Path}' alone: {reason}"));
            }
            else
            {
                checks[store] = new RunningProcessCheck(Inspector, store.Readers);
            }
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examination.Entries
                    .Where(entry => checks.ContainsKey(entry.Store))
                    .Select(entry => new DeletionTarget(
                        entry.Path,
                        $"The copies of {Path.GetFileName(entry.Path)} a debugger downloaded. It downloads them again "
                        + "when it needs them.",
                        DirectoryAge.Of(entry.Path, ct),
                        Group: entry.Store.Path,
                        UseCheck: checks[entry.Store])),
            ],
            keep,
            ct).ConfigureAwait(false);

        // A held store is named first, so §5.6 records it under the reason it was held for.
        var protectedPaths = Protect([.. held, .. examination.Survivors]);

        if (steps.Count == 0 && held.Count == 0 && examination.Declined.Count == 0 && !examination.Unreadable)
        {
            return Nothing(
                examination.Present.Count == 0
                    ? "No symbol store a debugger is set to use exists yet."
                    : "No symbol store holds a downloaded copy Deguffer can remove.",
                notes,
                protectedPaths);
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = protectedPaths,
            Notes = notes,
            Fallback = measured.Fallback,

            // A store held back while a debugger runs, and an entry behind a link, are something real
            // left unexamined.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examination.Declined.Count > 0),
            HasUnreadableRoot = examination.Unreadable,
        };
    }

    /// <summary>
    /// A plan with nothing to remove that still says what it found, and still asserts what it names: a store
    /// emptied by an earlier run keeps its markers and anything it did not recognise.
    /// </summary>
    private CleanupPlan Nothing(string why, IReadOnlyList<PlanNote> notes, IReadOnlyList<ProtectedPath> protectedPaths)
    {
        var empty = EmptyPlan(why);
        return empty with { Notes = [.. empty.Notes, .. notes], ProtectedPaths = protectedPaths };
    }
}
