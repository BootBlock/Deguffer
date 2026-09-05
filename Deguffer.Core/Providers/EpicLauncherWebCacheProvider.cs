using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches of the embedded browser the Epic Games launcher draws its store in (343 MB of a
/// 346 MB web cache folder on the measured machine).
///
/// <para><b>The folder is renamed by every launcher update, so the old ones stay.</b> Epic's own
/// support article names <c>webcache</c>, <c>webcache_4147</c> and <c>webcache_4430</c> and tells
/// the reader to delete whichever of them appear. That numbered suffix is why
/// <see cref="EpicLauncherSaved.WebCacheDirectory"/> is a pattern rather than a name, and why a
/// machine that has run the launcher for years can be holding several of these at once.</para>
///
/// <para><b>Deguffer deliberately reaches inside those folders instead of removing them, and Epic's
/// article is the reason it has to be said.</b> A web cache folder is a Chromium profile directory:
/// on the measured machine it held 289 MB of HTTP cache, 43 MB of service-worker responses and
/// 11 MB of compiled script, and beside them the store's <c>Cookies</c>, <c>Local Storage</c>,
/// <c>Session Storage</c> and <c>IndexedDB</c>. <see cref="ChromiumCacheProvider"/> already refuses
/// to take a profile directory whole for exactly that reason, and the rule does not change because
/// a different vendor wrote the folder. Epic's article is troubleshooting advice, where signing the
/// reader out is an acceptable price for fixing a broken launcher; Deguffer is reclaiming space,
/// where it is not. Naming the caches inside costs 3 MB of the 346 MB and keeps the user signed
/// in.</para>
///
/// <para>§5.1 does not apply. The launcher exposes no cache-eviction command, and the engine's own
/// clear-browsing-data surface is not reachable from inside it. Epic's published route is to delete
/// the folder, which is the path-based case §5.2 then governs.</para>
///
/// <para><b>The launcher's own build of the engine keeps Chromium's pre-M81 disk-cache layout</b>,
/// where the cache entries sit directly in <c>Cache</c> rather than in a <c>Cache_Data</c>
/// underneath it. That is why this table declares <c>Cache</c> disposable outright while Chromium's
/// declares it a container — see <see cref="Levels"/>, which is also where the difference between
/// that directory and <c>Service Worker</c> is argued.</para>
/// </summary>
public sealed class EpicLauncherWebCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// What may be removed from a web cache folder, one containing directory at a time.
    ///
    /// <para><b><c>Cache</c> goes whole and <c>Service Worker</c> does not, and the difference is
    /// what is known to sit beside the cache.</b> <c>Cache</c> is Chromium's HTTP disk cache and its
    /// entire content is cache entries, in this layout and in the newer one alike — nothing else has
    /// ever been written there, and the engine rebuilds the directory from nothing.
    /// <c>Service Worker</c> is not that: <c>Database</c> inside it is the register of which workers
    /// are installed for which pages, and removing that is not a cache eviction. So the register's
    /// parent becomes a level of its own and only the two caches inside it are named.</para>
    ///
    /// <para>Both containers are declared Tier 4 rather than left out, on
    /// <see cref="ChromiumCacheProvider"/>'s reasoning: the generic "not recognised" sentence would
    /// be actively false about a directory that really is left standing while something inside it
    /// really is being removed.</para>
    ///
    /// <para><c>GPUCache</c> is absent because the launcher does not write one. An unrecognised name
    /// is Tier 4 by construction, so a build that starts writing one reclaims nothing until somebody
    /// measures it — which is the direction §5.2 requires being wrong in.</para>
    /// </summary>
    public static readonly IReadOnlyList<CacheLevel> Levels =
    [
        new CacheLevel(string.Empty, new DisposableChildSet(
        [
            new ChildClassification(
                "Cache",
                SafetyTier.RegenerableCache,
                "Pages and pictures the store saved so it would not fetch the same thing twice. They "
                + "are downloaded again when they are next wanted."),
            new ChildClassification(
                "Code Cache",
                SafetyTier.RegenerableCache,
                "Compiled JavaScript and WebAssembly from the store's own pages. The launcher "
                + "recompiles each script the first time it runs again."),
            new ChildClassification(
                "Service Worker",
                SafetyTier.DoNotTouch,
                "The directory the store's background workers are registered in. Only the responses "
                + "and scripts they cached are removed, and the register itself stays."),
        ])),
        new CacheLevel("Service Worker", new DisposableChildSet(
        [
            new ChildClassification(
                "CacheStorage",
                SafetyTier.RegenerableCache,
                "Responses a background worker stored so the store would work offline. It fetches "
                + "them again the next time the launcher is online."),
            new ChildClassification(
                "ScriptCache",
                SafetyTier.RegenerableCache,
                "The background workers' own scripts, kept so they start without a download. The "
                + "launcher fetches them again."),
            new ChildClassification(
                "Database",
                SafetyTier.DoNotTouch,
                "Which background workers are registered for which of the store's pages. It is the "
                + "register rather than anything it cached, so it is left alone."),
        ])),
    ];

    /// <summary>
    /// The one file in a web cache folder that §5.6 must name. Every other thing worth sparing in
    /// there is a directory, so the enumeration classifies it and the plan asserts it — but a file
    /// is never enumerated, never classified and never asserted unless a provider names it, and this
    /// is the file the whole shape of this provider exists to protect.
    /// </summary>
    private const string SignInCookies = "Cookies";

    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private SavedFolderScan? _saved;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public EpicLauncherWebCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
    }

    public override string Id => "epic-launcher-webcache";

    public override string Name => "Epic Games launcher web cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The store fetches its pages and pictures from the network instead of from disk the first "
        + "time the launcher is opened again, and recompiles the scripts behind them, so the store "
        + "fills in more slowly once. You stay signed in, and nothing in your library changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Epic Games launcher",
        Publisher = "Epic Games",
        Purpose = "The launcher draws its store in a browser built into itself, and that browser "
            + "caches web pages, compiled scripts and offline responses under the launcher's own "
            + "folder. The folder is renamed every time the launcher updates its engine, so the "
            + "caches from previous versions are left sitting beside the current one.",
        Recommendation = "Epic's own advice is to delete those folders whole, which also signs you "
            + "out of the store. Deguffer names the caches inside them instead and leaves the "
            + "folder standing, because the sign-in cookies and the store's saved data are in "
            + "there too — which costs about three megabytes and keeps you signed in.",
    };

    /// <summary>§5.3. The launcher holds its embedded browser's cache open while it runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => EpicLauncherSaved.ProcessNames;

    /// <summary>The launcher's <c>Saved</c> folder. Exposed so tests can assert it is never a target.</summary>
    public string SavedPath => EpicLauncherSaved.PathIn(Environment);

    /// <summary>
    /// One look at the <c>Saved</c> folder, memoised for the life of a planning pass (G4). Presence,
    /// planning and <see cref="ToolRoots"/> all ask the same question of the same directory, and
    /// this is the one enumeration behind all three.
    ///
    /// Exposed so tests can assert what was and was not found.
    /// </summary>
    public SavedFolderScan Look(CancellationToken ct = default) => _saved ??= Examine(ct);

    /// <summary>The web cache folders on disk, which is what most callers want from a look.</summary>
    public IReadOnlyList<string> WebCaches(CancellationToken ct = default) => Look(ct).WebCaches;

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the <c>Saved</c> folder, then a root for each web
    /// cache folder and for the <c>Service Worker</c> directory inside it.
    ///
    /// <para>A root per level, on Cargo's and Chromium's reasoning — a declaration is an allow-list
    /// over one directory's <em>immediate</em> children, so the two caches that sit two deep need
    /// the directory holding them declared as well. Without it Explore would refuse the one thing
    /// this provider removes from in there.</para>
    ///
    /// <para>The web cache folders come from the enumeration, so a machine with none of them
    /// declares only the <c>Saved</c> folder — which still refuses the launcher's settings, and is
    /// the declaration that matters most.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??=
        [
            EpicLauncherSaved.Root(Environment),
            .. from cache in WebCaches()
               from level in Levels
               select ToolRoot.Of(
                   level.Resolve(cache),
                   "This is the folder the Epic Games launcher's store keeps its browser data in. "
                   + "Deguffer removes the caches inside it from the Storage page, where it knows "
                   + "which of them are caches — your sign-in cookies sit beside them.",
                   level.Children),
        ];

    public override void InvalidateCaches()
    {
        _saved = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a web cache folder actually being there. The <c>Saved</c> folder exists on every
    /// machine that has ever opened the launcher, so reading it as a hit would report this source
    /// and then plan nothing.
    ///
    /// <para><b>The three cases that are not a cache are here because the planner never asks an
    /// absent provider for a plan.</b> A link on the way down, a web cache folder that is itself a
    /// link, and a <c>Saved</c> folder that refuses to be listed each have their own sentence in
    /// <see cref="BuildPlanAsync"/>, and all three are unreachable if this answers false. The row
    /// then reads "Not installed" about a launcher that is installed, which is a stronger untruth
    /// than the "Already clear" it would otherwise be.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var saved = Look(ct);

        return Task.FromResult(
            saved.Link is not null
            || saved.Unreadable
            || saved.WebCaches.Count > 0
            || saved.LinkedWebCaches.Count > 0);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var look = Look(ct);

        // Before the existence check, not after it. A link partway up resolves onto a directory that
        // holds no launcher folder of its own, so Saved reads as absent and the pass would end
        // reporting nothing at all about a redirection it did detect.
        if (look.Link is { } link)
        {
            return UnexaminedPlan(
                $"Leaving '{link}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        if (!look.Exists)
        {
            return EmptyPlan("The Epic Games launcher has kept no folder in this user's account.");
        }

        var saved = SavedPath;
        var caches = look.WebCaches;
        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();

        var survivors = new List<(string Path, string Reason)>
        {
            (saved, "The launcher's own folder must survive — only recognised caches inside it are removed."),
        };

        survivors.AddRange(EpicLauncherSaved.ProtectedNames.Select(
            n => (Path.Combine(saved, n.Name), n.Reason)));

        // The folder was found on disk by name above, and a listing right is separate from a
        // traverse right — so a refusal here leaves a plan with no steps and, without this, nothing
        // said. The shell renders that as "Already clear", which is a claim about a folder nobody
        // read.
        if (look.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(saved));
        }

        foreach (var path in look.LinkedWebCaches)
        {
            notes.Add(LinkNote(path));
            declined.Add((path, LinkReason));
        }

        var unreadable = look.Unreadable;
        var emptiedAContainer = false;
        var spared = 0;

        foreach (var cache in caches)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                cache,
                "The store's browser folder itself must survive — only recognised caches inside it "
                + "are removed."));
            survivors.Add((
                Path.Combine(cache, SignInCookies),
                "The store's sign-in cookies. Removing them signs you out of the launcher's store."));

            var outcome = CollectFrom(cache, targets, declined, survivors, notes, ct);

            spared += outcome.Spared;
            emptiedAContainer |= outcome.EmptiedAContainer;
            unreadable |= outcome.Unreadable;
        }

        // One note rather than one per spared child. Each is still asserted individually by §5.6,
        // and this is the sentence that says so. The second half is not decoration: two of the
        // caches sit inside a directory that is itself kept, so a user who sees 'Service Worker'
        // still standing has no way to tell that anything inside it went.
        if (spared > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{spared} other {(spared == 1 ? "item is" : "items are")} left alone beside the "
                + "caches. Your sign-in and the store's saved data are in there, so only the "
                + "recognised cache directories are removed."
                + (emptiedAContainer
                    ? " Two of those caches sit inside a directory of their own — 'Service Worker' — "
                      + "and that directory stays: only the recognised caches inside it are removed."
                    : string.Empty)));
        }

        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                caches.Count == 0
                    ? "The Epic Games launcher's store has kept no web cache on this machine."
                    : "The Epic Games launcher's store is holding no cache on disk."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed. A warning that the
        // launcher is holding files open, on a row with nothing to delete, describes a clean that
        // will not happen.
        if (targets.Count > 0 && BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
                [.. survivors.Concat(declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };
    }

    /// <summary>
    /// What one listing of the <c>Saved</c> folder found, or the refusal that stopped it.
    ///
    /// <para>A link on the derived path is answered before anything is listed, because listing
    /// through it would return the far side's ordinary directories — and a recognised name among
    /// them would be targeted while every §5.6 survivor named for this folder resolved through the
    /// same link and passed.</para>
    ///
    /// <para>Only the links named like a web cache folder are kept. A link named <c>Config</c> is a
    /// child of the same folder, but it is not this provider's subject, and naming it on a row about
    /// web caches would put a sentence in front of the user with nothing to do with what the row
    /// removes.</para>
    /// </summary>
    private SavedFolderScan Examine(CancellationToken ct)
    {
        if (EpicLauncherSaved.FirstLinkTo(Environment) is { } link)
        {
            return new SavedFolderScan(link, [], [], Unreadable: false, Exists: true);
        }

        var saved = SavedPath;

        if (!LongPath.DirectoryExists(saved))
        {
            return new SavedFolderScan(null, [], [], Unreadable: false, Exists: false);
        }

        var scan = ChildDirectories.Under(saved);
        var found = new List<string>();

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (EpicLauncherSaved.WebCacheDirectory().IsMatch(child.Name))
            {
                found.Add(LongPath.Display(child.FullName));
            }
        }

        return new SavedFolderScan(
            null,
            found,
            [
                .. scan.Links
                    .Where(l => EpicLauncherSaved.WebCacheDirectory().IsMatch(l.Name))
                    .Select(l => LongPath.Display(l.FullName)),
            ],
            scan.Unreadable,
            Exists: true);
    }

    /// <summary>
    /// §5.2 for one web cache folder: classify the children of each level, target the recognised
    /// ones, and protect the rest. A spared child is a sibling of a targeted one under the same
    /// parent, which is exactly when an over-broad rule takes both — so it is asserted to survive
    /// rather than merely left out of the plan.
    /// </summary>
    private static LevelOutcome CollectFrom(
        string cache,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> declined,
        List<(string Path, string Reason)> survivors,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var spared = 0;
        var emptiedAContainer = false;
        var unreadable = false;

        // A container that is a link is met twice: once as a link child of the cache folder, and
        // once as a level whose own directory turns out to be one. Both times it is the same path
        // and the same sentence.
        var reportedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var level in Levels)
        {
            ct.ThrowIfCancellationRequested();

            var directory = level.Resolve(cache);

            if (!LongPath.DirectoryExists(directory))
            {
                continue;
            }

            // Applied at every level rather than only at the one reached by name. The cache folders
            // came from an enumeration that filtered links out, so this answers false for them
            // today — and that is the point: a safety property riding on a filter nobody named holds
            // only for as long as every target happens to arrive the same way.
            if (LongPath.IsReparsePoint(directory))
            {
                Decline(directory);
                continue;
            }

            var scan = ChildDirectories.Under(directory);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(directory));
                unreadable = true;
                continue;
            }

            foreach (var link in scan.Links)
            {
                Decline(LongPath.Display(link.FullName));
            }

            foreach (var child in scan.Directories)
            {
                var classification = level.Children.Classify(child.Name);
                var path = LongPath.Display(child.FullName);

                if (classification.Tier.IsOfferable())
                {
                    targets.Add(new DeletionTarget(path, classification.Reason));
                    emptiedAContainer |= level.ContainerName.Length > 0;
                }
                else
                {
                    survivors.Add((path, classification.Reason));
                    spared++;
                }
            }
        }

        return new LevelOutcome(spared, emptiedAContainer, unreadable);

        void Decline(string path)
        {
            if (!reportedLinks.Add(path))
            {
                return;
            }

            notes.Add(LinkNote(path));
            declined.Add((path, LinkReason));
        }
    }

    private static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");

    /// <summary>What one listing of the <c>Saved</c> folder found.</summary>
    /// <param name="Link">
    /// The first segment of the derived path down to <c>Saved</c> that is a link, or null when none
    /// of them is. Nothing below it was listed when this is set.
    /// </param>
    /// <param name="WebCaches">The web cache folders, in display form.</param>
    /// <param name="LinkedWebCaches">
    /// Children named like a web cache folder that turned out to be links. Reported rather than
    /// dropped: one is a child the user can see, and a plan that neither offers it nor mentions it
    /// disagrees with the folder.
    /// </param>
    /// <param name="Unreadable">
    /// The folder refused to be listed, so the two lists above describe nothing rather than
    /// describing a folder with nothing in it.
    /// </param>
    /// <param name="Exists">
    /// Whether the folder is there at all. Distinct from <paramref name="Unreadable"/>, because
    /// absence is a complete answer and a refusal is not an answer at all.
    /// </param>
    public readonly record struct SavedFolderScan(
        string? Link,
        IReadOnlyList<string> WebCaches,
        IReadOnlyList<string> LinkedWebCaches,
        bool Unreadable,
        bool Exists);

    /// <summary>What one web cache folder's levels came to, for the sentences the plan carries.</summary>
    /// <param name="Spared">
    /// How many children were spared. Counted here rather than from the length of the survivor list,
    /// which also carries the <c>Saved</c> folder, the cache folder and the named files — a total
    /// including those would tell the user that items were left alone in a folder that holds none.
    /// </param>
    /// <param name="EmptiedAContainer">
    /// Whether a target came from inside <c>Service Worker</c>. That directory is kept, so without
    /// this the user sees it still standing and cannot tell that the caches inside it went.
    /// </param>
    /// <param name="Unreadable">Whether a directory refused to be listed, so nothing was planned from it.</param>
    private readonly record struct LevelOutcome(int Spared, bool EmptiedAContainer, bool Unreadable);
}
