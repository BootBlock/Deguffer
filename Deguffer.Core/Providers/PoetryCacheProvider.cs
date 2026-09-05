using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Poetry's package caches. Researched rather than measured: no Poetry was installed on the machine
/// this was written against.
///
/// <para><b>The virtual environments live inside the cache directory, and that is the whole reason
/// this provider is careful.</b> <c>virtualenvs.path</c> defaults to <c>{cache-dir}/virtualenvs</c>,
/// so a rule that reclaimed the cache by removing the directory named <c>Cache</c> would take every
/// environment Poetry has created for every project on the machine, each one a re-resolve and a
/// re-download away from working again. That is §5.2 with configuration replaced by state, so the
/// root is never a target and <c>virtualenvs</c> is declared Tier 4 by name rather than left to fall
/// through as unrecognised.</para>
///
/// <para><b>§5.1's command answers half of the cache, and the half it answers is not the large
/// one.</b> <c>poetry cache clear</c> reaches <c>{cache-dir}/cache/repositories</c> and nothing
/// else: it builds a <c>FileCache</c> over that directory and flushes it. The downloaded archives
/// and the wheels Poetry built from source distributions sit in <c>{cache-dir}/artifacts</c>, which
/// no Poetry command removes — a gap Poetry's own issue tracker has carried for years, and the
/// reason its users are told to delete the folder by hand. So the plan is command-first where a
/// command exists and path-based for the one recognised child it cannot reach.</para>
///
/// <para><b>The command is run once per named cache rather than once with <c>--all</c>.</b> The
/// documented bare <c>poetry cache clear --all</c> is a recent spelling: until the cache argument
/// was made optional it failed outright with "Not enough arguments", so a plan built on it would
/// silently reclaim nothing on an older Poetry. <c>poetry cache list</c> names the caches its own
/// <c>clear</c> accepts, and passing those names back is the form that works on every version — and
/// it puts a size against each repository rather than one figure for all of them.</para>
/// </summary>
public sealed class PoetryCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The only children of Poetry's cache directory this provider recognises. Anything else is
    /// Tier 4 by construction — see <see cref="DisposableChildSet"/>.
    ///
    /// <para><c>virtualenvs</c> is declared rather than omitted. An unrecognised child is already
    /// left alone, but it is left alone under a sentence saying Deguffer did not know what it was,
    /// and this is the one child whose contents the user most needs named.</para>
    /// </summary>
    public static readonly DisposableChildSet DisposableChildren = new(
    [
        new ChildClassification(
            "artifacts",
            SafetyTier.RegenerableCache,
            "Package archives Poetry downloaded, and the wheels it built from source distributions. "
            + "The next install fetches or rebuilds them."),
        new ChildClassification(
            "cache",
            SafetyTier.RegenerableCache,
            "The package metadata Poetry caches per repository while it resolves dependencies. It is "
            + "fetched again on the next resolve."),
        new ChildClassification(
            "virtualenvs",
            SafetyTier.DoNotTouch,
            "Every virtual environment Poetry has created, for every project on this machine. Each "
            + "one is a full dependency install rather than a cache, so none of them is ever removed."),
    ]);

    /// <summary>No paths measured, for the routes that produce no command step at all.</summary>
    private static readonly ScanBatch NothingMeasured = new([], FallbackReason.None, []);

    private readonly PoetryDiscovery _discovery;

    public PoetryCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default) =>
        _discovery = new PoetryDiscovery(Runner);

    public override string Id => "poetry";

    public override string Name => "Poetry package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next poetry install re-downloads package archives, re-fetches the metadata Poetry "
        + "resolves against, and rebuilds any wheel Poetry had built from a source distribution. "
        + "Your virtual environments, and everything installed into them, are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Poetry, a dependency manager and packaging tool for Python",
        Publisher = "the Poetry project",
        Purpose = "Poetry keeps the archives it downloads, the wheels it builds from packages that "
            + "ship only as source, and the repository metadata it resolves against, in one folder "
            + "shared by every project. That same folder is where it puts the virtual environments "
            + "it creates.",
        Recommendation = "Deguffer clears the caches inside that folder and never the folder "
            + "itself, because your virtual environments sit beside them — one per project, each a "
            + "full install rather than a cache.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["poetry", "python", "python3"];

    /// <summary>Poetry's folder in the local profile. Never a target, and what §5.6 asserts survived.</summary>
    public string LocalRoot => Path.Combine(Environment.LocalAppData, "pypoetry");

    /// <summary>
    /// Where Poetry keeps its cache when it has not been asked. It is what the §5.2 declaration below
    /// is written against, because a declaration Explore consults on every path cannot run a
    /// subprocess, and it is the fallback handed to <see cref="PoetryDiscovery"/>.
    /// </summary>
    public string DefaultCacheRoot => Path.Combine(LocalRoot, "Cache");

    /// <summary>
    /// Poetry's folder in the roaming profile. Never reached into at all: it holds
    /// <c>config.toml</c>, the <c>auth.toml</c> that carries credentials for private package
    /// repositories, and the Python interpreters Poetry manages.
    /// </summary>
    public string RoamingRoot => Path.Combine(Environment.RoamingAppData, "pypoetry");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, and three roots because the trap is at three
    /// depths. The cache directory is the one with recognised children in it; the folder above it
    /// has none, so Explore refuses the directory that holds the environments as a unit; and the
    /// roaming folder is a root with nothing inside it that may ever go.
    ///
    /// <para>The cache directory Poetry reports is deliberately not declared. It arrives from a
    /// subprocess, and this is the documented default of the folder holding it.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Of(
            DefaultCacheRoot,
            "This is Poetry's cache folder. Deguffer clears the caches inside it and nothing else, "
            + "because every virtual environment Poetry has created sits in here beside them.",
            DisposableChildren),

        new ToolRoot(
            LocalRoot,
            "This is Poetry's own folder, and the cache inside it holds your virtual environments. "
            + "Deguffer never removes it as a unit.",
            static _ => false),

        new ToolRoot(
            RoamingRoot,
            "This is your Poetry configuration, and it may hold credentials for private package "
            + "repositories. Deguffer never removes it.",
            static _ => false),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("poetry") is not null);

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("poetry") is not { } poetry)
        {
            return EmptyPlan("Poetry is not installed on this machine.");
        }

        var (cacheRoot, environments) =
            await _discovery.DiscoverAsync(poetry, DefaultCacheRoot, ct).ConfigureAwait(false);

        if (!LongPath.DirectoryExists(cacheRoot))
        {
            return EmptyPlan($"Poetry is installed but its cache directory does not exist yet ({cacheRoot}).");
        }

        // The enumeration below never classifies the directory it is handed. A junctioned cache
        // directory would hand back the far side's ordinary children, which would be targeted while
        // every survivor named here resolved through the same link and passed.
        if (LongPath.IsReparsePoint(cacheRoot))
        {
            return UnexaminedPlan(
                $"Leaving '{LongPath.Display(cacheRoot)}' alone: it is a link to somewhere else, and "
                + "Deguffer does not look through a link.");
        }

        // §5.2, and the reason this is a check on the resolved paths rather than on the child names
        // below. cache-dir and virtualenvs.path are configured independently, so a value naming the
        // cache directory itself, or anything above it, would make everything under it part of the
        // environment tree — and the name-based rule would not see it.
        if (LongPath.Contains(environments, cacheRoot))
        {
            return UnexaminedPlan(
                $"Poetry keeps its virtual environments at {LongPath.Display(environments)}, which "
                + "holds its cache rather than sitting inside it. Deguffer is leaving the whole of "
                + $"{LongPath.Display(cacheRoot)} alone.");
        }

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Poetry reports its cache directory as {LongPath.Display(cacheRoot)} and its virtual "
                + $"environments as {LongPath.Display(environments)}."),
        };

        var (targets, scan) = CollectTargets(cacheRoot, environments, notes, ct);
        var (deletions, deleted) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        var (commands, cleared) = await PlanRepositoryClearsAsync(
            poetry, cacheRoot, environments, notes, ct).ConfigureAwait(false);

        // Built rather than returned as an empty plan, so the sentences explaining what was left
        // alone survive: EmptyPlan replaces the note list rather than adding to it, and a link or an
        // unreadable root is exactly the case where the reason is the whole of the answer.
        var offersNothing = commands.Count == 0 && deletions.Count == 0;

        if (offersNothing && !scan.Unreadable && scan.Links.Count == 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Poetry is installed but has cached nothing yet."));
        }

        // §5.5's fallback is a property of the volume rather than of one path, so the first reason
        // either measurement met is the one the user is shown.
        var fallback = cleared.Fallback != FallbackReason.None ? cleared.Fallback : deleted.Fallback;

        // Said once, and that is why this provider builds the sentence rather than taking each
        // batch's own note. It is the only provider that measures twice — the command route and the
        // path route, over two children of one directory — so both batches meet the same volume and
        // report the same reason, and adding both put the same paragraph on screen twice, reading as
        // two separate findings about one scan.
        if (FallbackReasonText.Describe(fallback) is { } route)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, route));
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,

            // §5.1's route first: where Poetry can evict its own cache, that is what the user is
            // offered, and the path-based step is the one child no Poetry command reaches.
            Steps = [.. commands, .. deletions],
            ProtectedPaths = BuildProtectedPaths(cacheRoot, environments),
            Notes = notes,
            Fallback = fallback,
            HasUnreadableRoot = scan.Unreadable,

            // A cache reached through a link is present, measures nothing here, and holds
            // everything. Rendering that as "Already clear" would disagree with the folder the user
            // can see — see CleanupPlan.WasNotExamined.
            WasNotExamined = offersNothing && (scan.Unreadable || scan.Links.Count > 0),
        };
    }

    /// <summary>
    /// §5.6. The environments are the subject: they sit inside the directory being cleaned, so a
    /// run has to produce evidence that they are still there rather than leaving it to be inferred
    /// from the absence of a step naming them. The roaming folder is named for the credentials in
    /// <c>auth.toml</c>, which are nowhere near the cache and would be a silent loss if a future
    /// rule ever reached the wrong <c>pypoetry</c>.
    ///
    /// <para>The repository caches are deliberately not protected. Poetry's own command removes each
    /// one rather than emptying it, and recreates it on next use, so asserting their survival would
    /// fail verification on a successful run.</para>
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(string cacheRoot, string environments) => Protect(
        (cacheRoot, "Poetry's cache directory must survive — only the caches within it are cleared."),
        (environments, "Every virtual environment Poetry has created. Each is a full install, not a cache."),
        (Path.Combine(cacheRoot, "cache"), "The directory holding the repository caches; only the caches inside it are cleared."),
        (Path.Combine(cacheRoot, "cache", "repositories"), "The directory holding one cache per repository; only the caches inside it are cleared."),
        (LocalRoot, "Poetry's folder in your profile, which is what contains the cache."),
        (RoamingRoot, "Your Poetry configuration, which may hold credentials for private package repositories."),
        (Path.Combine(RoamingRoot, "auth.toml"), "Stored credentials for private package repositories."),
        (Path.Combine(RoamingRoot, "config.toml"), "Your Poetry configuration."));

    /// <summary>
    /// The recognised children of the cache directory that Deguffer removes itself, with everything
    /// else named to the user rather than silently omitted (§5.2).
    ///
    /// <para><c>cache</c> is recognised and is still not a target. The repository caches inside it
    /// are cleared by Poetry's own command, and §5.1 leaves that route in charge of them, so
    /// deleting the directory holding them would be Deguffer doing by path what the tool was about
    /// to do properly.</para>
    /// </summary>
    private (IReadOnlyList<DeletionTarget> Targets, ChildDirectoryScan Scan) CollectTargets(
        string cacheRoot,
        string environments,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var scan = ChildDirectories.Under(cacheRoot);
        var targets = new List<DeletionTarget>();

        // A listing right is separate from the traverse right that found the directory, so a refusal
        // would otherwise leave a plan with no steps and nothing said — which the shell renders as
        // "Already clear", a claim about a folder nobody read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(cacheRoot));
        }

        notes.AddRange(scan.Links.Select(link => new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does not "
            + "delete through a link.")));

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = DisposableChildren.Classify(child.Name);

            if (!classification.Tier.IsOfferable())
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: {classification.Reason}"));
                continue;
            }

            if (child.Name.Equals("cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Enumeration runs in extended form; a plan always holds display paths.
            var path = LongPath.Display(child.FullName);

            // The same §5.2 check the whole-root case above makes, one level in. A virtualenvs.path
            // configured inside a recognised child would otherwise be deleted by a step that named
            // a cache.
            if (LongPath.Contains(path, environments))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Warning,
                    $"Leaving '{child.Name}' alone: Poetry keeps its virtual environments inside it, "
                    + "and those are never removed."));
                continue;
            }

            targets.Add(new DeletionTarget(
                path,
                classification.Reason,

                // Poetry nests an artefact under four levels of its URL hash before it reaches a
                // file, so the top level moves only when a hash prefix is first seen. A cache filled
                // every day would report as years old, which is backwards for the one thing an age
                // is read for — see DeclaredLocation.ReportsAge for the same call on Maven.
                LastWritten: null));
        }

        return (targets, scan);
    }

    /// <summary>
    /// One §5.1 command step per cache Poetry names, measured against the directory that cache
    /// occupies.
    ///
    /// <para><c>--no-interaction</c> is not decoration. <c>poetry cache clear</c> asks "Delete N
    /// entries?" before it does anything, and Deguffer starts it with no console attached, so
    /// without the flag the step would depend on what a detached standard input does to a prompt.
    /// The flag makes Poetry take the prompt's own default, which is yes. It overrides no safety
    /// check of Poetry's: §7's confirmation has already been given by the time this runs.</para>
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanRepositoryClearsAsync(
        string poetry,
        string cacheRoot,
        string environments,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var repositories = Path.Combine(cacheRoot, "cache", "repositories");

        if (!LongPath.DirectoryExists(repositories))
        {
            return ([], NothingMeasured);
        }

        // Poetry's clear never reaches outside its repository cache, so this is the whole of the
        // §5.2 question for the command route rather than a per-cache one.
        if (LongPath.Contains(repositories, environments))
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "Poetry keeps its virtual environments inside its own repository cache, so Deguffer "
                + "is not running its cache clear command."));

            return ([], NothingMeasured);
        }

        var named = await _discovery.ListCachesAsync(poetry, ct).ConfigureAwait(false);

        var caches = named
            .Select(name => (Name: name, Path: Path.Combine(repositories, name)))
            .Where(cache => LongPath.DirectoryExists(cache.Path))
            .ToList();

        if (caches.Count == 0)
        {
            // The directory is there and Poetry named nothing in it, so something is present that
            // Deguffer is not going to reclaim. §5.5 wants a route that could not be taken said out
            // loud rather than rendered as an empty row.
            if (ChildDirectories.Under(repositories).Directories.Count > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    "Poetry did not name a cache its own clear command accepts, so its repository "
                    + "cache is left alone."));
            }

            return ([], NothingMeasured);
        }

        var measured = await MeasureAllAsync([.. caches.Select(c => c.Path)], ct).ConfigureAwait(false);

        // Zipped rather than indexed: pairing a cache with the wrong size would attribute one
        // repository's bytes to another command, and nothing downstream could tell.
        IReadOnlyList<CleanupStep> steps =
        [
            .. caches.Zip(measured.Sizes, (cache, size) => (CleanupStep)new RunCommandStep(
                poetry,
                $"cache clear {cache.Name} --all --no-interaction --no-ansi",
                $"Clear Poetry's cached metadata for {cache.Name} using its own command")
            {
                Estimated = size,
                MeasuredPaths = [cache.Path],
            }),
        ];

        return (steps, measured);
    }
}
