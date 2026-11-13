using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The global package cache of Yarn 2 and later (Berry). Researched rather than measured: no Yarn was
/// installed on the machine this was written against, so every behaviour here is read from
/// yarnpkg/berry (see <see cref="YarnBerryCache"/>) and run against Yarn's release bundles in a
/// scratch folder.
///
/// <para><b>Tier 2, because Plug'n'Play runs from it.</b> Yarn 4 turns <c>enableGlobalCache</c> on, and
/// a Plug'n'Play project's <c>.pnp.cjs</c> then points into the archives in this cache rather than
/// copying them
/// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/plugin-pnp/sources/PnpLinker.ts#L173-L175">PnpLinker.ts</a>).
/// Removing it stops every such project until <c>yarn install</c> runs in it, which downloads its
/// packages again. Yarn keeps no record of which projects use the cache and never prunes it
/// (<a href="https://github.com/yarnpkg/berry/issues/2775">yarnpkg/berry#2775</a>), so nothing can
/// tell an archive a project still runs from apart from one nobody needs. For Yarn 2 and 3 the same
/// folder is a mirror projects copy from, and removing it costs only downloads.</para>
///
/// <para><b>A recorded exception to §5.1.</b> Berry's <c>yarn cache clean</c> deletes the whole cache
/// folder, which is what this row removes, but no way of running it is safe from Deguffer's folder.
/// Yarn 2 and 3 fail on every form of it outside a project. Yarn 4's deletes whatever folder its
/// settings name. And Yarn 1, which a <c>yarnPath</c> setting or a Corepack shim can put behind the
/// same <c>yarn</c> between the preview and the clean, ignores options it does not know and would
/// clear its own cache instead. So the folder is removed by path, and only when every entry in it is a
/// file Yarn writes there (<see cref="YarnBerryFolder"/>).</para>
///
/// <para><b>§5.2.</b> The global folder is never a target. Only its <c>cache</c> is recognised. The
/// hard-link <c>store</c>, Yarn 4's <c>metadata</c> and <c>index</c>, <c>telemetry.json</c> and
/// anything else beside it stay, and each is asserted afterwards. A project's own <c>.yarn\cache</c>
/// is never looked at: a Zero-Installs project commits it to version control.</para>
/// </summary>
public sealed class YarnBerryCacheProvider : CleanupProviderBase
{
    private const string GlobalFolderReason =
        "This is the global folder of Yarn 2 and later. Beside its cache it keeps the store projects' "
        + "node_modules files are linked from, the package metadata it has fetched, and its own settings. "
        + "Deguffer removes nothing here except the cache.";

    private const string CacheReason =
        "This is the global cache of Yarn 2 and later. Deguffer removes it from the Storage page, and only "
        + "when everything in it is a package archive Yarn named itself.";

    private const string SiblingReason =
        "Kept by Yarn beside its cache. Deguffer does not recognise it as cache.";

    private readonly ISystemDirectories _system;

    /// <summary>Berry's answer through the memory, for what this row declares to Explore and to a duplicate search.</summary>
    private readonly PassMemo<Task<YarnBerryCache?>> _remembered = new();

    /// <summary>
    /// Which Yarn runs and Berry's settings, asked afresh, for the plan alone. A memo of its own,
    /// because a declaration read first in the pass would otherwise hand the plan a remembered version
    /// or global folder, and this row deletes by path under it (§5.2), where a missed input would move
    /// a deletion rather than misstate a figure.
    /// </summary>
    private readonly PassMemo<Task<YarnBerryCache?>> _planned = new();

    private readonly ToolAnswerMemory _memory;

    /// <param name="environment">The profile, the executables and the variables Yarn is started with.</param>
    /// <param name="runner">Starts Yarn.</param>
    /// <param name="inspector">Tells whether Yarn or Node.js is running.</param>
    /// <param name="scanner">Measures the cache.</param>
    /// <param name="system">Windows' own folders, which a configured global folder must not be.</param>
    /// <param name="volumes">The volumes, which a configured global folder must not be the root of.</param>
    /// <param name="memory">
    /// Where Yarn's answers about which Yarn it is and where its global folder is are kept while
    /// nothing Yarn read has changed: the memory for <paramref name="runner"/> when null, which is the
    /// process-wide one for the real runner. The plan asks Yarn afresh whatever it holds.
    /// </param>
    public YarnBerryCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        ToolAnswerMemory? memory = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _memory = memory ?? ToolAnswerMemory.For(runner);
    }

    public override string Id => "yarn-berry";

    public override string Name => "Yarn global package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Every Yarn 4 project that uses Plug'n'Play stops working until you run yarn install in it, which "
        + "downloads its packages again. Projects with a node_modules folder or their own .yarn\\cache folder "
        + "keep working, and their next install downloads what it needs.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Yarn 2 and later (Berry), a package manager for Node.js",
        Publisher = "the open-source Yarn project",
        Purpose = "Yarn keeps every package it downloads as a zip in one cache all your projects share, and "
            + "Yarn 4's Plug'n'Play projects run their packages straight from those zips. Yarn never removes "
            + "anything from it, so it grows with every package version any project has used.",
        Recommendation = "Clear it when you need the space and can run yarn install in your projects "
            + "afterwards. Deguffer removes it only when everything in it is a package archive Yarn named "
            + "itself, and leaves the rest of Yarn's folder alone.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["node", "yarn"];

    /// <summary>Berry's global folder where nothing moves it.</summary>
    public string DefaultGlobalFolder => YarnBerryCache.DefaultGlobalFolder(Environment);

    /// <summary>§5.2 as §7.1 reads it: the global folder recognises its cache alone, and the cache its archives alone.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => RootsOf(DefaultGlobalFolder);

    /// <summary>The global folder Berry reports, where it is not the default.</summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        ReportedFolder(await RememberedAsync(ct).ConfigureAwait(false)) is { Accepted: { } folder } ? RootsOf(folder) : [];

    /// <summary>
    /// The default cache and the one Berry reports, because a setting can move the global folder back
    /// after a duplicate search has asked. Each whole: the row removes the cache whole.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        [.. GlobalFolders(ReportedFolder(await RememberedAsync(ct).ConfigureAwait(false))).Select(YarnBerryCache.CacheIn).Select(CleanedPlace.Whole)];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(
            Environment.FindExecutable("yarn") is not null
            || LongPath.DirectoryMayExist(YarnBerryCache.CacheIn(DefaultGlobalFolder)));

    /// <summary>
    /// Which Yarn runs, and where it keeps its global folder, are configuration that can change between
    /// scans. The pass's answers are forgotten here, and the memory keeps Yarn's answers only while
    /// everything <see cref="YarnToolInputs"/> declares, and the environment Yarn is started with, read
    /// the same. The plan asks Yarn afresh in any case.
    /// </summary>
    public override void InvalidateCaches()
    {
        _remembered.Forget();
        _planned.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var notes = new List<PlanNote>();
        var yarn = Environment.FindExecutable("yarn");
        var answer = yarn is null ? null : await _planned.Get(() => FreshAnswerAsync(yarn, ct)).ConfigureAwait(false);

        if (answer is { Settings.CleanDisabled: true })
        {
            return EmptyPlan(
                $"Yarn {answer.Version.Text} is set not to clean its cache (enableCacheClean is false), so Deguffer "
                + "leaves the cache alone as well.");
        }

        if (yarn is not null && answer is null)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information,
                "Yarn did not say which version it is, so Deguffer looked only in the default folder of Yarn 2 and later."));
        }
        else if (answer is { Version.IsBerry: false })
        {
            // The usual Corepack machine: Yarn 1 outside any project, Yarn 4 inside them. Only Berry can
            // say where its global folder was moved to.
            notes.Add(new PlanNote(PlanNoteSeverity.Information,
                $"The yarn on your PATH runs Yarn {answer.Version.Text}, which cannot say where Yarn 2 and later keep "
                + "their global folder, so Deguffer looked only in the default one."));
        }

        var reported = ReportedFolder(answer);

        if (reported is { Declined: { } declined })
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, declined));
        }

        var found = GlobalFolders(reported).Select(YarnBerryFolder.Read).ToList();

        if (found.All(folder => folder.State is YarnBerryCacheState.Absent))
        {
            var nothing = EmptyPlan(
                $"Yarn 2 and later have cached nothing for this user ({YarnBerryCache.CacheIn(DefaultGlobalFolder)} does not exist).");

            return nothing with { Notes = [.. notes, .. nothing.Notes] };
        }

        notes.AddRange(found.Select(NoteFor).OfType<PlanNote>());

        var targets = found
            .Where(folder => folder.State is YarnBerryCacheState.Recognised)
            .Select(folder => new DeletionTarget(
                folder.Cache,
                "The global cache of Yarn 2 and later: package archives Yarn downloads again when a project needs them.",
                UseCheck: new YarnBerryCacheCheck(folder.GlobalFolder)))
            .ToList();

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

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
            ProtectedPaths = await ProtectAsync(
                [
                    .. found.Where(folder => folder.State is not YarnBerryCacheState.Absent).SelectMany(Survivors),
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = found.Any(folder => folder.State is YarnBerryCacheState.Refused),
        };
    }

    /// <summary>
    /// The global folder and everything beside its cache, and the cache too where this row leaves it.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> Survivors(YarnBerryFolder folder) =>
    [
        (folder.GlobalFolder, GlobalFolderReason),
        .. folder.Siblings.Select(sibling => (sibling, SiblingReason)),
        .. folder.State is YarnBerryCacheState.Recognised or YarnBerryCacheState.Empty
            ? []
            : new[] { (folder.Cache, "The global cache of Yarn 2 and later, which Deguffer left alone.") },
    ];

    private static PlanNote? NoteFor(YarnBerryFolder folder) => folder switch
    {
        { State: YarnBerryCacheState.Refused, Cause: { } refused } => UnreadableRoot.Note(refused),
        { State: YarnBerryCacheState.Link } => new PlanNote(PlanNoteSeverity.Information,
            $"Leaving '{folder.Cause}' alone: it is a link to somewhere else, and Deguffer does not look through a link."),
        { State: YarnBerryCacheState.Stranger } => new PlanNote(PlanNoteSeverity.Information,
            $"Yarn's cache folder {folder.Cache} holds '{folder.Cause}', which Yarn does not write there, and the "
            + "cache goes whole. Deguffer is leaving the cache alone."),
        { State: YarnBerryCacheState.Empty } => new PlanNote(PlanNoteSeverity.Information,
            $"The global cache of Yarn 2 and later in {folder.Cache} is empty."),
        _ => null,
    };

    private static IReadOnlyList<ToolRoot> RootsOf(string globalFolder) =>
    [
        ToolRoot.Folders(globalFolder, GlobalFolderReason, static name => name.Equals("cache", StringComparison.OrdinalIgnoreCase)),
        new ToolRoot(
            YarnBerryCache.CacheIn(globalFolder),
            CacheReason,
            static child => child.Kind == ChildKind.File && YarnBerryCache.IsCacheFile(child.Name)),
    ];

    /// <summary>The default global folder, and the one Berry reports where this row accepts it.</summary>
    private IReadOnlyList<string> GlobalFolders((string? Accepted, string? Declined)? reported) =>
        reported is { Accepted: { } folder } ? [DefaultGlobalFolder, folder] : [DefaultGlobalFolder];

    /// <summary>
    /// The global folder Berry reported in <paramref name="answer"/> where it is not the default:
    /// accepted, or declined with the sentence that says why. Null where Berry was not asked or named
    /// the default.
    /// </summary>
    private (string? Accepted, string? Declined)? ReportedFolder(YarnBerryCache? answer)
    {
        if (answer is not { Settings.GlobalFolder: { } reported } || Reach(reported).IsSameAs(Reach(DefaultGlobalFolder)))
        {
            return null;
        }

        return ConfiguredFolder.WhyNotOwned(
                reported, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned
            ? (null, $"Yarn is set to keep its global folder in '{reported}', and Deguffer will not look for its cache "
                + $"there: {notOwned} It is leaving it alone.")
            : (reported, null);
    }

    /// <summary>Berry's answer through the memory, once per pass, for the declarations. Null where there is no yarn.</summary>
    private Task<YarnBerryCache?> RememberedAsync(CancellationToken ct) =>
        Environment.FindExecutable("yarn") is { } yarn
            ? _remembered.Get(() => RememberedAnswerAsync(yarn, ct))
            : Task.FromResult<YarnBerryCache?>(null);

    /// <summary>
    /// Which Yarn runs and, for Berry, its settings, each from the memory while nothing Yarn read has
    /// changed. Null where Yarn would not say which it is.
    /// </summary>
    private async Task<YarnBerryCache?> RememberedAnswerAsync(string yarn, CancellationToken ct)
    {
        var which = await YarnVersionAnswer.AskAsync(_memory, Environment, Runner, yarn, ct).ConfigureAwait(false);

        return which.Version is not { } version ? null
            : !version.IsBerry ? new YarnBerryCache(version, Settings: null)
            : new YarnBerryCache(version, await _memory.AskAsync(SettingsQuestion(yarn, which), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Which Yarn runs and, for Berry, its settings, each from Yarn now whatever the memory holds: the
    /// plan deletes by path under the global folder (§5.2), and a remembered version could skip the
    /// settings that move the folder or turn the clean off. Null where Yarn would not say which it is.
    /// </summary>
    private async Task<YarnBerryCache?> FreshAnswerAsync(string yarn, CancellationToken ct)
    {
        var which = await YarnVersionAnswer.AskAfreshAsync(_memory, Environment, Runner, yarn, ct).ConfigureAwait(false);

        return which.Version is not { } version ? null
            : !version.IsBerry ? new YarnBerryCache(version, Settings: null)
            : new YarnBerryCache(version, await _memory.AskAfreshAsync(SettingsQuestion(yarn, which), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Berry's settings, which depend on the script <paramref name="which"/> named, so they are declared
    /// with that script and kept only where Yarn said which script it runs.
    /// </summary>
    private ToolQuestion<YarnBerrySettings> SettingsQuestion(string yarn, YarnVersionAnswer which) =>
        new(
            $"config get globalFolder enableCacheClean --json|{yarn}",
            Environment.ToolEnvironment,
            _ => YarnToolInputs.Berry(Environment, yarn, which.Script),
            answer => answer.Answered && which.IsAnswer,
            askCt => YarnBerrySettings.AskAsync(Runner, yarn, askCt));
}
