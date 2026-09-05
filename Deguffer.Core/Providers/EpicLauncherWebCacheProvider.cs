using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches of the embedded browser the Epic Games launcher draws its store in (339 MB of a
/// 343 MB web cache folder on the measured machine).
///
/// <para><b>The folder is renamed by every launcher update, so the old ones stay.</b> Epic's own
/// support article names <c>webcache</c>, <c>webcache_4147</c> and <c>webcache_4430</c> and tells
/// the reader to delete whichever of them appear. That numbered suffix is why
/// <see cref="EpicLauncherSaved.WebCacheDirectory"/> is a pattern rather than a name, and why a
/// machine that has run the launcher for years can be holding several of these at once.</para>
///
/// <para><b>Deguffer deliberately reaches inside those folders instead of removing them, and Epic's
/// article is the reason it has to be said.</b> A web cache folder is a Chromium profile directory:
/// on the measured machine it held 287 MB of HTTP cache, 41 MB of service-worker responses and
/// 10 MB of compiled script, and beside them the store's <c>Cookies</c>, <c>Local Storage</c>,
/// <c>Session Storage</c> and <c>IndexedDB</c>. <see cref="ChromiumCacheProvider"/> already refuses
/// to take a profile directory whole for exactly that reason, and the rule does not change because
/// a different vendor wrote the folder. Epic's article is troubleshooting advice, where signing the
/// reader out is an acceptable price for fixing a broken launcher; Deguffer is reclaiming space,
/// where it is not. Naming the caches inside costs 4 MB of the 343 MB and keeps the user signed
/// in.</para>
///
/// <para>§5.1 does not apply. The launcher exposes no cache-eviction command, and the engine's own
/// clear-browsing-data surface is not reachable from inside it. Epic's published route is to delete
/// the folder, which is the path-based case §5.2 then governs.</para>
///
/// <para><b>The cache entries sit directly in <c>Cache</c> on the measured machine</b>, rather than
/// in a <c>Cache_Data</c> underneath it as a current Chromium build writes them. That is why this
/// table declares <c>Cache</c> disposable outright while Chromium's declares it a container — see
/// <see cref="Levels"/>, which is also where the difference between that directory and
/// <c>Service Worker</c> is argued, and why the difference does not matter either way.</para>
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
    /// The credential surface inside a web cache folder, named in full rather than sampled.
    ///
    /// <para>Named at all because child classification enumerates directories, so anything that is a
    /// file is never seen, never classified and never asserted unless a provider names it. These are
    /// the reason this provider reaches into a web cache folder rather than removing it, and an
    /// assertion that the folder survived would pass with every one of them gone.</para>
    ///
    /// <para>In full rather than sampled, on <see cref="ChromiumCacheProvider"/>'s reasoning:
    /// anything less makes the §5.6 evidence weaker than the claim it supports. A web cache folder
    /// is a Chromium profile directory and the launcher's store takes payment, so the whole set
    /// belongs here even though the measured machine held only the cookie. <c>Cookies</c> is listed
    /// at both paths because Chromium moved it under <c>Network</c> and an older profile keeps it at
    /// the top level; whichever is absent records itself as nothing to preserve rather than as a
    /// pass.</para>
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedProfileFiles =
    [
        ("Cookies", "The store's sign-in cookies. Removing them signs you out of the launcher's store."),
        (@"Network\Cookies", "The store's sign-in cookies. Removing them signs you out of the launcher's store."),
        ("Login Data", "Any username and password the store's browser saved."),
        ("Web Data", "Any address and payment card the store's browser saved."),
    ];

    private WebCacheScan? _scan;
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

    /// <summary>The launcher's <c>Saved</c> folder.</summary>
    private string SavedPath => EpicLauncherSaved.PathIn(Environment);

    /// <summary>
    /// One look at the <c>Saved</c> folder, memoised for the life of a planning pass (G4). Presence,
    /// planning and <see cref="ToolRoots"/> all ask the same question of the same directory, and
    /// this is the one enumeration behind all three.
    /// </summary>
    private WebCacheScan Look(CancellationToken ct = default) => _scan ??= Examine(ct);

    /// <summary>
    /// The web cache folders on disk, which is what most callers want from a look. Exposed so tests
    /// can assert which folders were and were not entered.
    /// </summary>
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
        _scan = null;
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
        var scan = Look(ct);

        return Task.FromResult(
            scan.Folder.HasSomethingToReport
            || scan.WebCaches.Count > 0
            || scan.LinkedWebCaches.Count > 0);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var look = Look(ct);

        // Before the existence check, not after it. A link partway up resolves onto a directory that
        // holds no launcher folder of its own, so Saved reads as absent and the pass would end
        // reporting nothing at all about a redirection it did detect.
        if (look.Folder.Link is { } link)
        {
            return UnexaminedPlan(
                $"Leaving '{link}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        if (!look.Folder.Exists)
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
        if (look.Folder.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(saved));
        }

        foreach (var path in look.LinkedWebCaches)
        {
            notes.Add(CacheLevelWalk.Note(path));
            declined.Add((path, CacheLevelWalk.LinkReason));
        }

        var unreadable = look.Folder.Unreadable;
        var emptiedAContainer = false;
        var spared = 0;

        foreach (var cache in caches)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                cache,
                "The store's browser folder itself must survive — only recognised caches inside it "
                + "are removed."));
            survivors.AddRange(ProtectedProfileFiles.Select(
                file => (Path.Combine(cache, file.Name), file.Reason)));

            var walk = CacheLevelWalk.Under(Levels, cache, ct);

            targets.AddRange(walk.Targets);
            declined.AddRange(walk.Declined);
            survivors.AddRange(walk.Survivors);
            notes.AddRange(walk.Notes);

            spared += walk.Spared;
            emptiedAContainer |= walk.EmptiedAContainer;
            unreadable |= walk.Unreadable;
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
            // One sentence, not two. Reaching here needs no target, no declined link and a
            // readable folder, and the only remaining way presence could have been true is a web
            // cache folder actually being there — so an "and no web cache folder either" arm would
            // be a branch for a case the planner never asks about.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "The Epic Games launcher's store is holding no cache on disk."));
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
    /// The shared look at the <c>Saved</c> folder, with this provider's own subject picked out of
    /// it.
    ///
    /// <para>Only the children named like a web cache folder are kept, links included. A child named
    /// <c>Config</c> belongs to the same folder, but it is not this provider's subject, and naming it
    /// on a row about web caches would put a sentence in front of the user with nothing to do with
    /// what the row removes.</para>
    /// </summary>
    private WebCacheScan Examine(CancellationToken ct)
    {
        var folder = EpicLauncherSaved.Look(Environment);
        var found = new List<string>();

        foreach (var child in folder.Children)
        {
            ct.ThrowIfCancellationRequested();

            if (EpicLauncherSaved.WebCacheDirectory().IsMatch(child.Name))
            {
                found.Add(LongPath.Display(child.FullName));
            }
        }

        return new WebCacheScan(
            folder,
            found,
            [
                .. folder.Links
                    .Where(l => EpicLauncherSaved.WebCacheDirectory().IsMatch(l.Name))
                    .Select(l => LongPath.Display(l.FullName)),
            ]);
    }

    /// <summary>The shared look at the <c>Saved</c> folder, plus this provider's subject in it.</summary>
    /// <param name="Folder">Whether the folder is reachable, readable and there, and what it holds.</param>
    /// <param name="WebCaches">The web cache folders, in display form.</param>
    /// <param name="LinkedWebCaches">
    /// Children named like a web cache folder that turned out to be links. Reported rather than
    /// dropped: one is a child the user can see, and a plan that neither offers it nor mentions it
    /// disagrees with the folder.
    /// </param>
    private readonly record struct WebCacheScan(
        EpicLauncherSaved.SavedFolder Folder,
        IReadOnlyList<string> WebCaches,
        IReadOnlyList<string> LinkedWebCaches);

}

