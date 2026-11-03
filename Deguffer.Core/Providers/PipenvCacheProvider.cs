using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// pipenv's own package cache, which it keeps apart from pip's. Researched from pipenv's source
/// rather than measured: no pipenv was installed on the machine this was written against.
///
/// <para><b>The default is <c>%LOCALAPPDATA%\pipenv\pipenv\Cache</c>, not the folder pipenv's
/// documentation names.</b> <c>environments.py</c> resolves it with platformdirs'
/// <c>user_cache_dir("pipenv")</c>, which on Windows puts the application's name in twice, once as
/// its author. <see cref="CacheVariable"/> moves it.</para>
///
/// <para><b>Two formats share the folder, and only one of them has a command.</b> pipenv hands the
/// folder to pip as its cache directory, so <c>http</c>, <c>http-v2</c> and <c>wheels</c> are pip's
/// own layout, and <c>pip --cache-dir &lt;folder&gt; cache purge</c> empties exactly those three: it
/// reads the directory from the option, and nothing else in the folder. That is the §5.1 route
/// wherever pip can be found. <c>pipenv-manifests</c> is pipenv's own resolver cache, which no
/// command clears on its own, so it is removed by path. Where pip cannot be found, pip's three
/// folders are removed by path as well, because what is in them is the same either way.</para>
///
/// <para><b>pipenv's own <c>--clear</c> is not the step.</b> It deletes the whole of this folder and
/// then the whole of pip's user cache, which is another row's to clear and reaches past this one
/// (<c>routines/clear.py</c>).</para>
/// </summary>
public sealed class PipenvCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move pipenv's cache.</summary>
    public const string CacheVariable = "PIPENV_CACHE_DIR";

    /// <summary>
    /// The children of pipenv's cache this provider recognises. Anything else, such as the
    /// <c>pkgs</c> folder pipenv names to pip as a download destination, is Tier 4 by construction.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "http",
            SafetyTier.RegenerableCache,
            "Package archives pip downloaded for pipenv, in the format older versions of pip wrote. pipenv "
            + "downloads them again when it next needs them."),
        new ChildClassification(
            "http-v2",
            SafetyTier.RegenerableCache,
            "Package archives and index pages pip downloaded for pipenv. pipenv downloads them again when "
            + "it next needs them."),
        new ChildClassification(
            "wheels",
            SafetyTier.RegenerableCache,
            "Wheels pip built for pipenv from packages that ship only as source. pipenv builds them again, "
            + "which for a package with C extensions means compiling it."),
        new ChildClassification(
            "pipenv-manifests",
            SafetyTier.RegenerableCache,
            "The package index pages and package metadata pipenv's resolver has read. pipenv reads them from "
            + "the index again at its next lock."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    private const string ToolRootReason =
        "This is pipenv's package cache. Deguffer clears the caches inside it and never the folder itself.";

    private readonly ISystemDirectories _system;
    private readonly RowDeclarations _declarations;

    /// <param name="declarations">
    /// What every row in the planner offers, so a child another row removes from a folder this row
    /// walks is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public PipenvCacheProvider(
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

    public override string Id => "pipenv";

    public override string Name => "pipenv package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next pipenv install or lock re-downloads packages and index pages, and rebuilds any wheel "
        + "pip had built for pipenv from source, which for a package with C extensions means compiling "
        + "it again. Your virtual environments, Pipfiles and lock files are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "pipenv, a dependency manager for Python projects",
        Publisher = "the Python Packaging Authority",
        Purpose = "pipenv keeps its own copy of what pip downloads and builds for it, and of the package "
            + "indexes its resolver reads, in a folder of its own beside pip's cache, so the next lock "
            + "or install does not fetch them again.",
        Recommendation = "pip's own command clears the part of the folder that is in pip's format, and "
            + "Deguffer removes pipenv's resolver cache beside it. Your virtual environments live "
            + "elsewhere and are untouched.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["pipenv", "pip", "python", "python3"];

    /// <summary>Where pipenv keeps its cache when <see cref="CacheVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(LocalRoot, "pipenv", "Cache");

    /// <summary>pipenv's folder in the local profile. Never a target, and what §5.6 asserts survived.</summary>
    public string LocalRoot => Path.Combine(Environment.LocalAppData, "pipenv");

    /// <summary>
    /// The cache, honouring <see cref="CacheVariable"/>, or null where that names nothing this can
    /// examine as pipenv's. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveRoot() => Resolve().Folder;

    /// <summary>
    /// The cache in use, and the default one as well where the variable moved it, because a cache left
    /// at the default is still pipenv's. A folder the variable names but this declines is not declared:
    /// pipenv keeps no configuration there to spare.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Roots().Select(root => ToolRoot.Of(root, ToolRootReason, Children)),
    ];

    /// <summary>
    /// The recognised children of the cache in use and of the default cache, whichever route clears
    /// them, because both routes clear the same folders.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Roots().SelectMany(root => Children.DisposableNames
                .Select(child => CleanedPlace.Whole(Path.Combine(root, child)))),
        ]);

    /// <summary>
    /// Presence is a cache on the disk, never pipenv on the PATH: a cache outlives the pipenv that wrote
    /// it, and nothing here runs pipenv.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root
            && Children.DisposableNames.Any(name => LongPath.DirectoryMayExist(Path.Combine(root, name))));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Folder is not { } root)
        {
            return EmptyPlan(
                $"{CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as pipenv's "
                + $"cache: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(root, $"pipenv has not cached anything for this user — no {root} directory.") is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, from a variable or a default, so nothing has classified it, and a
        // recognised name on the far side of a link would be cleared through it.
        if (LongPath.IsReparsePoint(root))
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, root, ct);
        var spared = await walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false);

        List<PlanNote> notes = [.. walk.Notes, .. spared.Select(CacheLevelWalk.SparedNote)];

        var pip = PipCommand.Find(Environment);
        var pipFolders = walk.Targets.Where(target => PipLayoutTarget.Folders.Contains(Path.GetFileName(target.Path))).ToList();

        // pip's purge reaches every one of its three folders that is there, so it is sent only where the
        // walk accepted all of them. One the walk declined as a link would be emptied through the link.
        var purge = pip is not null
            && pipFolders.Count > 0
            && !walk.Declined.Any(declined => PipLayoutTarget.Folders.Contains(Path.GetFileName(declined.Path)));

        if (pip is null && pipFolders.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Deguffer cannot find pip, so it removes the part of pipenv's cache that is in pip's format "
                + "by path rather than with pip's own command."));
        }

        var (deletions, deleted) = await PlanDeletionsAsync(
            purge ? [.. walk.Targets.Where(target => !PipLayoutTarget.Folders.Contains(Path.GetFileName(target.Path)))] : walk.Targets,
            keep,
            ct).ConfigureAwait(false);

        IReadOnlyList<CleanupStep> commands = [];
        var fallback = deleted.Fallback;

        if (purge)
        {
            IReadOnlyList<string> probed = [.. pipFolders.Select(target => target.Path)];
            var measured = await MeasureAllAsync(probed, ct).ConfigureAwait(false);

            commands =
            [
                new RunCommandStep(
                    pip!,
                    $"--cache-dir \"{LongPath.Display(root)}\" cache purge",
                    "Clear the part of pipenv's cache that is in pip's format, using pip's own command")
                {
                    // Each entry in pip's cache is one file, so a purge ended part-way leaves whole entries or none.
                    WhenCancelled = CancelledCommand.Ended,
                    Estimated = measured.Total,
                    MeasuredPaths = probed,
                    TargetCheck = new PipLayoutTarget(root),
                },
            ];

            fallback = FallbackReasonText.Prevailing(measured.Fallback, fallback);
        }

        // Said once for both measurements, which meet the same volume and give the same reason.
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

            // §5.1's route first, then what no command reaches.
            Steps = [.. commands, .. deletions],
            ProtectedPaths = Protect(
                walk,
                spared,
                (root, "pipenv's cache folder itself must survive — only the caches inside it are cleared."),
                (LocalRoot, "pipenv's folder in your profile, which contains its default cache.")),
            Notes = notes,
            Fallback = fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }

    private IEnumerable<string> Roots() =>
        new[] { ResolveRoot(), DefaultRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    private ConfiguredFolder.Setting Resolve() =>
        ConfiguredFolder.FromVariable(CacheVariable, DefaultRoot, Environment, _system, Volumes);
}
