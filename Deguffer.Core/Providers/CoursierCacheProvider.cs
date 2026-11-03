using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Coursier's artefact cache, the JARs and metadata that sbt, Scala CLI, Mill and Metals resolve
/// through Coursier, shared by every one of them on the machine. Researched rather than measured: no
/// Coursier installation was present on the machine this was written against.
///
/// <para><b>Where it is.</b> <c>CoursierPaths.computeCacheDirectoryFrom</c> gives each of Coursier's
/// caches its own variable, then its own Java property, then a folder of its own inside the
/// platform cache directory, <c>%LOCALAPPDATA%\Coursier\Cache</c> on Windows. The artefact cache is
/// <c>v1</c>, moved by <see cref="CacheVariable"/>, and the variable names that folder itself
/// rather than the one around it. A Java property is set per command, on a JVM's command line, and
/// is written down nowhere Deguffer can read, so a cache moved only by one is not found: the
/// failure is a smaller reclaim, never a wrong target.</para>
///
/// <para><b>What is in it.</b> <c>CachePath.localFile</c> lays an artefact out under its address,
/// scheme first, so the folders Coursier makes in the cache are <c>https</c> and <c>http</c>. Those
/// two are recognised and anything else in the cache is Tier 4. Coursier reads a <c>file:</c>
/// repository in place rather than copying it, so nothing in either was built on this machine.</para>
///
/// <para><b>§5.2, and the reason this is a provider rather than a line in a table.</b> The artefact
/// cache's siblings are not downloads. <c>arc</c> is the archive cache, and since Coursier 2.1
/// <c>JvmCache</c> extracts every Java runtime that <c>cs java</c> and <c>cs setup</c> install into
/// it, where <c>JAVA_HOME</c> then points. <c>jvm</c> is where releases before that put them.
/// <c>digest</c> holds container image layers, and <c>cs docker build</c> imports the layers it
/// builds there, which exist on no remote. <c>priv</c> holds layers unpacked with elevated rights,
/// which a running container is built on. Each is Tier 4 and asserted to survive (§5.6) wherever its
/// own variable puts it, and a cache named by a variable that overlaps one of them offers nothing
/// inside the overlap.</para>
///
/// <para><b>§5.1 was asked and the answer is no.</b> Coursier has no command that clears its cache,
/// so this is the path-based case.</para>
/// </summary>
public sealed class CoursierCacheProvider : CleanupProviderBase
{
    /// <summary>Moves the artefact cache, and names the cache folder itself.</summary>
    public const string CacheVariable = "COURSIER_CACHE";

    /// <summary>
    /// The children of the artefact cache this provider recognises, one per scheme Coursier
    /// downloads over. Anything else is Tier 4 by construction.
    /// </summary>
    public static readonly DisposableChildSet ArtefactChildren = new(
    [
        new ChildClassification(
            "https",
            SafetyTier.RegenerableCache,
            "Artefacts Coursier downloaded over HTTPS. It downloads them again when a build next needs them."),
        new ChildClassification(
            "http",
            SafetyTier.RegenerableCache,
            "Artefacts Coursier downloaded over HTTP. It downloads them again when a build next needs them."),
    ]);

    /// <summary>
    /// Coursier's caches that are not downloads, each with the variable that moves it and the folder
    /// it has in <see cref="DefaultCacheHome"/> where that is unset.
    /// </summary>
    public static readonly IReadOnlyList<KeptCache> KeptCaches =
    [
        new(
            "COURSIER_ARCHIVE_CACHE",
            "arc",
            "Archives Coursier extracted, among them the Java runtimes 'cs java' and 'cs setup' installed. "
            + "JAVA_HOME may point into it."),
        new(
            "COURSIER_JVM_CACHE",
            "jvm",
            "Java runtimes that older Coursier releases installed. JAVA_HOME may point into it."),
        new(
            "COURSIER_DIGEST_BASED_CACHE",
            "digest",
            "Container image layers, among them ones 'cs docker build' made on this computer, which exist "
            + "nowhere else."),
        new(
            "COURSIER_PRIVILEDGED_ARCHIVE_CACHE",
            "priv",
            "Container image layers Coursier unpacked with elevated rights, which a running container may be "
            + "built on."),
    ];

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, ArtefactChildren)];

    private const string HomeReason =
        "This is Coursier's own folder. Deguffer removes the artefacts Coursier downloaded and nothing else, "
        + "because the Java runtimes it installed and the applications 'cs install' put on your path are "
        + "here too.";

    private const string ArtefactCacheReason =
        "This is Coursier's artefact cache. Deguffer removes the downloads inside it, filed by address, and "
        + "nothing else.";

    private readonly ISystemDirectories _system;
    private readonly RowDeclarations _declarations;

    /// <param name="declarations">
    /// What every row in the planner offers, so a child another row removes from a folder this row
    /// walks is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public CoursierCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        RowDeclarations? declarations = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _declarations = declarations ?? new RowDeclarations();
    }

    public override string Id => "coursier";

    public override string Name => "Coursier artefact cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next sbt, Scala CLI, Mill or Metals build downloads the dependencies it needs again, then runs "
        + "normally. The Java runtimes Coursier installed are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Coursier, the dependency fetcher behind sbt, Scala CLI, Mill and Metals",
        Publisher = "the Coursier project",
        Purpose = "Coursier keeps every library a Scala or Java build resolves through it, in one cache "
            + "that all of those tools share. Nothing prunes it, so it grows with every version a "
            + "project has used.",
        Recommendation = "Everything removed is a download that comes back at the next build. Deguffer "
            + "never removes the folder beside it where Coursier keeps the Java runtimes it installed, "
            + "because JAVA_HOME may point there.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames =>
        ["java", "cs", "coursier", "scala-cli", "scala", "mill", "bloop"];

    /// <summary>The folder holding Coursier's caches and installed applications.</summary>
    public string CoursierHome => Path.Combine(Environment.LocalAppData, "Coursier");

    /// <summary>The folder each of Coursier's caches has a folder in, where its variable is unset.</summary>
    public string DefaultCacheHome => Path.Combine(CoursierHome, "Cache");

    /// <summary>Where the artefact cache is when <see cref="CacheVariable"/> is unset.</summary>
    public string DefaultArtefactCache => Path.Combine(DefaultCacheHome, "v1");

    /// <summary>
    /// The artefact cache, honouring <see cref="CacheVariable"/>, or null where that names nothing
    /// this can examine as Coursier's. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveArtefactCache() => Resolve(CacheVariable, DefaultArtefactCache).Folder;

    /// <summary>
    /// The way down from <see cref="CoursierHome"/> to the default artefact cache, so Explore refuses
    /// the Java runtimes and the installed applications beside it, then the artefact cache in use and
    /// the default one, each recognising only its downloads. A kept cache a variable moved somewhere
    /// this accepts is declared as well, recognising nothing, so Explore refuses it there too. One
    /// this declines is not: the folder is not Coursier's to speak for.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. ToolRoot.WayDown(CoursierHome, [DefaultArtefactCache], HomeReason),
        .. ArtefactCaches().Select(cache => ToolRoot.Of(cache, ArtefactCacheReason, ArtefactChildren)),
        .. KeptCaches
            .Select(kept => (Kept: kept, Setting: Resolve(kept.Variable, Path.Combine(DefaultCacheHome, kept.Name))))
            .Where(moved => moved.Setting.Value is not null && moved.Setting.Folder is not null)
            .Select(moved => ToolRoot.Folders(moved.Setting.Folder!, moved.Kept.Reason, static _ => false)),
    ];

    /// <summary>
    /// The downloads in the artefact cache in use and in the default one, because the variable can be
    /// unset again after a duplicate search has read it. Only those: nothing else in either is cleaned.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. ArtefactCaches().SelectMany(cache => ArtefactChildren.DisposableNames
                .Select(child => CleanedPlace.Whole(Path.Combine(cache, child)))),
        ]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveArtefactCache() is { } cache && LongPath.DirectoryMayExist(cache));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve(CacheVariable, DefaultArtefactCache);

        if (setting.Folder is not { } cache)
        {
            return EmptyPlan(
                $"{CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as Coursier's "
                + $"cache: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(cache, $"Coursier has not downloaded anything for this user — no {cache} directory.") is { } nothing)
        {
            return nothing;
        }

        if (LongPath.IsReparsePoint(cache))
        {
            return UnexaminedPlan(
                $"Leaving '{cache}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var kept = KeptLocations();
        var walk = CacheLevelWalk.Under(Levels, cache, ct);

        // A variable can put one of the kept caches inside a download folder, or the artefact cache
        // inside a kept one. Either way the download folder's address layout is no longer evidence that
        // everything in it is a download, so it is spared whole.
        List<DeletionTarget> targets = [];
        List<(string Path, string Reason)> overlapping = [];

        foreach (var target in walk.Targets)
        {
            if (kept.FirstOrDefault(k => LongPath.Contains(target.Path, k.Path) || LongPath.Contains(k.Path, target.Path)) is { Path: not null } overlap)
            {
                overlapping.Add((target.Path, $"It overlaps '{overlap.Path}', which is not a download. {overlap.Reason}"));
            }
            else
            {
                targets.Add(target);
            }
        }

        // A kept cache can be a child of the artefact cache where the variable names the folder around
        // them all. It is said once, with what it holds rather than that it was not recognised.
        var unrecognised = await walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false);

        List<(string Path, string Reason)> spared =
        [
            .. unrecognised.Where(child => !kept.Any(k => LongPath.Contains(k.Path, child.Path) && LongPath.Contains(child.Path, k.Path))),
            .. overlapping,
        ];

        List<PlanNote> notes =
        [
            .. walk.Notes,
            .. spared.Select(CacheLevelWalk.SparedNote),
            .. kept
                .Where(k => LongPath.ProbeDirectory(k.Path) is PathPresence.Present)
                .Select(CacheLevelWalk.SparedNote),
        ];

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
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
            Steps = steps,
            ProtectedPaths = Protect(
                walk,
                spared,
                [
                    (cache, "Coursier's artefact cache itself must survive — only the downloads inside it are removed."),
                    (DefaultCacheHome, "The folder holding Coursier's caches must survive — only downloads inside it are removed."),
                    .. kept,
                ]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = targets.Count == 0 && (walk.Declined.Count > 0 || overlapping.Count > 0),
        };
    }

    /// <summary>
    /// Where each kept cache is, wherever its variable puts it, including a folder this would decline to
    /// examine: asserting that something survived needs no claim that the folder is Coursier's. A
    /// relative value names nowhere Deguffer can find, so that cache is left out.
    /// </summary>
    private IReadOnlyList<(string Path, string Reason)> KeptLocations() =>
    [
        .. KeptCaches
            .Select(kept => (Setting: Resolve(kept.Variable, Path.Combine(DefaultCacheHome, kept.Name)), kept.Reason))
            .Select(kept => (Path: kept.Setting.Value is null ? kept.Setting.Folder : kept.Setting.Named, kept.Reason))
            .Where(kept => kept.Path is not null)
            .Select(kept => (kept.Path!, kept.Reason)),
    ];

    private IEnumerable<string> ArtefactCaches() =>
        new[] { ResolveArtefactCache(), DefaultArtefactCache }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private ConfiguredFolder.Setting Resolve(string variable, string defaultFolder) =>
        ConfiguredFolder.FromVariable(variable, defaultFolder, Environment, _system, Volumes);

    /// <summary>One of Coursier's caches that holds something other than downloads.</summary>
    /// <param name="Variable">The environment variable that moves it, and names the folder itself.</param>
    /// <param name="Name">Its folder in <see cref="DefaultCacheHome"/> where the variable is unset.</param>
    /// <param name="Reason">What it holds, written for the user.</param>
    public sealed record KeptCache(string Variable, string Name, string Reason);
}
