using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Pulumi's plugin cache: every resource provider, language host, policy analyzer, converter and tool
/// Pulumi downloaded for a stack, one folder per kind, name and version, all kept. A cloud provider
/// such as AWS, Azure or Kubernetes is hundreds of megabytes a version. Researched rather than
/// measured: Pulumi was not installed on the machine this was written against, so every rule here is
/// from Pulumi's source (<c>sdk/go/common/workspace/plugins.go</c> and <c>paths.go</c>,
/// <c>pkg/cmd/pulumi/plugin/plugin_remove.go</c>) and its documentation.
///
/// <para><b>Tier 1, one item per plugin.</b> Pulumi's own words for its remove: "If a deleted plugin is
/// subsequently required to execute a Pulumi program, the Pulumi CLI will attempt to download and
/// install it automatically."</para>
///
/// <para><b>§5.1: Pulumi's own <c>plugin rm</c>, one plugin at a time.</b> It is given the plugin's kind,
/// name and exact version, so a plugin the user leaves out is one no command names. It removes the
/// folder and the files Pulumi keeps beside it. It asks before it removes unless told <c>--yes</c>, and
/// Deguffer gives a tool nothing on its standard input, so the flag is part of the step. Where Pulumi
/// is not installed, which is the commonest way to be left with a cache, each plugin is removed by
/// path, folder and files together, because that is all the command does
/// (<c>PluginInfo.Delete</c>).</para>
///
/// <para><b>Running Pulumi does more than remove.</b> Every command may ask whether a newer Pulumi
/// exists, and Deguffer cannot set a variable for a tool to stop it. Nothing it does besides reaches
/// the plugin cache.</para>
///
/// <para><b>§5.2.</b> Only a folder named as Pulumi names the plugins it installs is recognised, with its
/// <c>.lock</c> and <c>.lastused</c> files. A plugin still installing, an older Pulumi's <c>.tmp</c>
/// extraction folder, and anything else in the cache is Tier 4. So is everything else in Pulumi's
/// folder: <c>credentials.json</c>, the workspaces, templates, policy packs and the CLI itself, and,
/// for a stack kept in the local backend, the stack's own state, history and backups.</para>
///
/// <para><b>In use.</b> Pulumi starts a plugin from its folder in the cache, so a plugin a program runs
/// from is held back, and while <c>pulumi</c> itself runs the whole cache is. See
/// <see cref="PulumiPluginUse"/>.</para>
/// </summary>
public sealed class PulumiPluginProvider : CleanupProviderBase
{
    /// <summary>Moves Pulumi's whole folder, the plugin cache with it (<c>env.Home</c>).</summary>
    public const string HomeVariable = "PULUMI_HOME";

    /// <summary>The plugin cache inside Pulumi's folder (<c>workspace.PluginDir</c>).</summary>
    public const string PluginsFolder = "plugins";

    /// <summary>
    /// What Pulumi keeps in its folder, for a folder the variable names that this declines to examine:
    /// Pulumi still writes these there.
    /// </summary>
    private static readonly string[] NamesInTheHome =
    [
        PluginsFolder, CredentialsFile, "workspaces", "templates", "policies", "bin", "stacks", "history", "backups", "locks", "meta.yaml",
    ];

    private const string CredentialsFile = "credentials.json";

    private const string HomeRootReason =
        "This is Pulumi's own folder. It holds your Pulumi access tokens and may hold your stacks' state, so "
        + "Deguffer removes nothing in it but the plugins in its plugin cache.";

    private const string CacheRootReason =
        "This is Pulumi's plugin cache. Deguffer removes the plugins inside it and never the folder itself.";

    private const string InstallingReason = "A plugin Pulumi has not finished installing.";

    private const string DeclinedRootReason =
        "PULUMI_HOME names this folder, so Pulumi keeps its access tokens, its plugins and its stacks' state here.";

    private readonly ISystemDirectories _system;
    private readonly ILiveTreeInspector _liveTrees;

    public PulumiPluginProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "pulumi-plugins";

    public override string Name => "Pulumi plugin cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>Each plugin is one version of one provider, which a stack names on its own.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next Pulumi command that needs a removed plugin downloads and installs it again. Your stacks, "
        + "your projects and your Pulumi access tokens are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Pulumi, which manages cloud infrastructure from code",
        Publisher = "Pulumi Corporation",
        Purpose = "Pulumi downloads a plugin for every cloud provider and version a stack uses, and keeps every "
            + "one of them. A cloud provider's plugin is often hundreds of megabytes, and each upgrade adds another.",
        Recommendation = "Deguffer lists each plugin and removes the ones you choose with Pulumi's own 'pulumi "
            + "plugin rm', or by path where Pulumi is no longer installed, and offers nothing while Pulumi is running.",
    };

    /// <summary>Pulumi's folder where <see cref="HomeVariable"/> is unset: <c>.pulumi</c> in the profile.</summary>
    public string DefaultHome => Path.Combine(Environment.UserProfile, ".pulumi");

    /// <summary>
    /// §5.2 for Explore: each Pulumi folder opens only the way down to its plugin cache, and the cache
    /// recognises only the plugins that have finished installing and their files.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. DeclaredHomes.SelectMany(home => ToolRoot.WayDown(home, [PluginsIn(home)], HomeRootReason)),
        .. DeclaredHomes.Select(PluginsIn).Select(cache => new ToolRoot(cache, CacheRootReason, child => IsRecognised(cache, child))),
    ];

    /// <summary>
    /// What Explore must refuse that only the machine as it is now can say: the whole cache while Pulumi
    /// runs or while what runs from it cannot be read in full, each plugin a program runs from, and what
    /// Pulumi keeps in a folder the variable names that this declines.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> caches = [.. DeclaredHomes.Select(PluginsIn)];
        var findings = PulumiIsRunning() ? null : _liveTrees.FindLiveChildren(caches, ct);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. findings is null
                ? caches.Select(cache => new ToolRoot(cache, PulumiPluginUse.PulumiRunningReason, static _ => false))
                : findings.Live.Select(live => new ToolRoot(live.Directory, PulumiPluginUse.InUseReason, static _ => false)),
            .. caches
                .Where(cache => findings is not null && !findings.IsAnswered(cache))
                .Select(cache => new ToolRoot(cache, LiveTreeVeto.UncheckedReason, static _ => false)),
            .. Readings()
                .Select(setting => setting.DeclinedFolder)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(declined => ToolRoot.Sparing(declined, DeclinedRootReason, NamesInTheHome)),
        ]);
    }

    /// <summary>The plugin cache of every Pulumi folder either reading of the variable names, whole.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. DeclaredHomes.Select(home => CleanedPlace.Whole(PluginsIn(home)))]);

    /// <summary>
    /// A plugin cache on the disk, never Pulumi on the PATH: a cache outlives the Pulumi that filled it,
    /// and is removed by path once Pulumi is gone.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredHomes.Any(home => LongPath.DirectoryMayExist(PluginsIn(home))));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var pulumi = Environment.FindExecutable("pulumi");

        // Pulumi's command acts on the folder its own environment names, and a removal by path on the one
        // the next Pulumi the user starts reads.
        var setting = Resolve(pulumi is null ? VariableReading.Current : VariableReading.Tool);

        if (setting.Folder is not { } home)
        {
            return UnexaminedPlan(
                $"{HomeVariable} is set to '{setting.Value}', and Deguffer will not treat that as Pulumi's folder: "
                + $"{setting.Declined} None of its plugins is offered.");
        }

        var plugins = PluginsIn(home);

        if (NothingToPlanFor(plugins, $"Pulumi has downloaded no plugins for this account: {plugins} does not exist.", out var pluginsIsLink) is { } nothing)
        {
            return nothing;
        }

        // The folder arrives by name, from a variable or a default, so nothing has classified it.
        if (pluginsIsLink)
        {
            return UnexaminedPlan(
                $"Leaving '{plugins}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        if (PulumiPluginCache.Read(plugins) is not { } cache)
        {
            return UnreadableRootPlan(plugins);
        }

        var choice = PulumiPluginChoice.Make(cache, pulumi is not null, PulumiIsRunning(), _liveTrees, ct);
        var notes = new List<PlanNote>(choice.Notes);

        var (steps, measured, recent) = pulumi is null
            ? await PlanRemovalsByPathAsync(cache, choice.Offered, keep, ct).ConfigureAwait(false)
            : await PlanCommandsAsync(pulumi, cache, choice.Offered, ct).ConfigureAwait(false);

        if (recent.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {recent.Count} {(recent.Count == 1 ? "plugin" : "plugins")} alone: a plugin goes whole or not at all, "
                + $"and something in {(recent.Count == 1 ? "it" : "each")} {keep.DescribeChange()}."));
        }

        if (cache.Installed.Count == 0 && cache.Installing.Count == 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, "Pulumi has no plugins installed in its plugin cache."));
        }

        if (pulumi is null && steps.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Pulumi is not installed, so Deguffer removes each plugin by path, its folder and the files Pulumi "
                + "keeps beside it together, which is all Pulumi's own remove does."));
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
            // Held first, so a plugin held back is named for why rather than as something not offered.
            ProtectedPaths = await ProtectAsync(
                [
                    .. choice.Held,
                    .. recent.Select(plugin => (plugin.Folder, $"Something in this plugin {keep.DescribeChange()}, and it goes whole or not at all.")),
                    .. BuildProtectedPaths(home, cache, steps),
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = steps.Count == 0 && (choice.Held.Count > 0 || recent.Count > 0),
        };
    }

    /// <summary>One <c>pulumi plugin rm</c> a plugin, each told the plugin's exact kind, name and version.</summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured, IReadOnlyList<PulumiPlugin> Recent)> PlanCommandsAsync(
        string pulumi,
        PulumiPluginCache cache,
        IReadOnlyList<PulumiPlugin> offered,
        CancellationToken ct)
    {
        var measured = await MeasureAllAsync([.. offered.Select(plugin => plugin.Folder)], ct).ConfigureAwait(false);

        IReadOnlyList<CleanupStep> steps =
        [
            .. offered.Zip(measured.Sizes, (plugin, size) => new RunCommandStep(
                pulumi,
                plugin.RemoveArguments,
                "Remove this plugin using Pulumi's own command")
            {
                // Pulumi removes the folder before its files, and lists any folder without a .partial file
                // as installed, so a removal ended part-way would leave a plugin Pulumi tries to run.
                WhenCancelled = CancelledCommand.Finishes,
                Estimated = size,
                MeasuredPaths = [plugin.Folder],
                Removes = plugin.Folder,
                TargetCheck = new PulumiPluginTarget(new PulumiPluginUse(Inspector, _liveTrees, plugin)),
                LastWritten = cache.LastUsedUtc(plugin),
                Identity = IdentityOf(plugin),
                Facets = FacetsOf(plugin),
                Group = GroupOf(plugin),
            }),
        ];

        return (steps, measured, []);
    }

    /// <summary>
    /// Each plugin's folder, whole or not at all, and then the files Pulumi keeps beside it, which share
    /// the folder's identity so the plugin is one choice and one keep.
    ///
    /// <para><b>A plugin the guard on recently changed files touches is held back whole.</b> A removal
    /// that kept a recent file in the folder would leave a plugin Pulumi lists and cannot start, and one
    /// that kept only a recent <c>.lastused</c> would say a plugin that is gone was used. So such a
    /// plugin is returned as recent rather than offered in part.</para>
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured, IReadOnlyList<PulumiPlugin> Recent)> PlanRemovalsByPathAsync(
        PulumiPluginCache cache,
        IReadOnlyList<PulumiPlugin> offered,
        MinimumAge keep,
        CancellationToken ct)
    {
        var targets = offered.SelectMany(plugin =>
        {
            var use = new PulumiPluginUse(Inspector, _liveTrees, plugin);
            var lastUsed = cache.LastUsedUtc(plugin);

            return cache.SidecarsOf(plugin)
                .Select(file => new DeletionTarget(
                    file, "A file Pulumi keeps beside this plugin, which goes with it.", lastUsed, TargetKind.File,
                    Identity: IdentityOf(plugin), IsLeftover: true, Facets: FacetsOf(plugin), Group: GroupOf(plugin), UseCheck: use))
                .Prepend(new DeletionTarget(
                    plugin.Folder, "A plugin Pulumi downloads and installs again when a stack next needs it.", lastUsed,
                    Identity: IdentityOf(plugin), Facets: FacetsOf(plugin), Group: GroupOf(plugin), UseCheck: use));
        }).ToList();

        var (planned, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        var recentFiles = await RecentFileSteps.KeptAsync([.. planned.OfType<DeleteFileStep>()], keep, Scanner.Tuning, ct).ConfigureAwait(false);

        var recentKeys = planned
            .OfType<DeleteStep>()
            .Where(step => step.WithheldRecent)
            .Concat(recentFiles)
            .Select(step => step.Identity!.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CleanupStep> steps =
        [
            .. planned
                .Where(step => !recentKeys.Contains(step.Identity!.Key))
                // A plugin missing some of its files is one Pulumi still lists, and then fails to start.
                .Select(step => step is DeleteDirectoryStep folder ? folder with { IsAllOrNothing = true } : step),
        ];

        return (steps, measured, [.. offered.Where(plugin => recentKeys.Contains(IdentityOf(plugin).Key))]);
    }

    /// <summary>
    /// §5.6. Pulumi's folder and everything in it beside the plugin cache, the access tokens first; the
    /// cache itself; and everything in the cache that no step removes, which for Pulumi's command is the
    /// plugin's folder and the files beside it.
    /// </summary>
    private static (string Path, string Reason)[] BuildProtectedPaths(string home, PulumiPluginCache cache, IReadOnlyList<CleanupStep> steps)
    {
        var removed = new HashSet<string>(
            cache.Installed
                .Where(plugin => steps.Any(step => step.Subjects.Contains(plugin.Folder, StringComparer.OrdinalIgnoreCase)))
                .SelectMany(plugin => cache.SidecarsOf(plugin).Prepend(plugin.Folder)),
            StringComparer.OrdinalIgnoreCase);

        return
        [
            (home, "Pulumi's own folder must survive. Only plugins in its plugin cache are removed."),
            (Path.Combine(home, CredentialsFile), "Your Pulumi access tokens."),
            (cache.Folder, "Pulumi's plugin cache must survive: only the plugins chosen are removed from it."),
            .. cache.Installing.Select(plugin => (plugin.Folder, InstallingReason)),
            .. cache.Entries
                .Select(entry => LongPath.Display(entry.FullName))
                .Where(path => !removed.Contains(path))
                .Select(path => (path, "Not a plugin Deguffer offers, so it must survive.")),
            .. (FolderEntries.Of(home) ?? [])
                .Select(entry => LongPath.Display(entry.FullName))
                .Where(path => !path.Equals(cache.Folder, StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, "Beside the plugin cache in Pulumi's folder, and nothing removes it.")),
        ];
    }

    private static ItemIdentity IdentityOf(PulumiPlugin plugin) => new(Path.GetFileName(plugin.Folder), plugin.Title);

    private static IReadOnlyList<ItemFacet> FacetsOf(PulumiPlugin plugin) => [new ItemFacet("Version", plugin.Version)];

    /// <summary>One heading a plugin, so every version of the AWS provider is listed together.</summary>
    private static string GroupOf(PulumiPlugin plugin) => $"{plugin.Name} ({plugin.Kind})";

    /// <summary>
    /// A plugin's folder, or one of the files Pulumi keeps beside it, as the plan would offer it: a
    /// recognised name whose plugin has finished installing. Only the <c>.partial</c> file beside the
    /// folder says it has not, so the disk is asked as Explore asks, and a file Windows will not
    /// describe counts as there.
    /// </summary>
    private static bool IsRecognised(string cache, ToolRootChild child)
    {
        var plugin = child.Kind switch
        {
            ChildKind.Folder => child.Name,
            ChildKind.File => PulumiPlugin.SidecarSuffixes
                .Where(suffix => suffix != PulumiPlugin.PartialSuffix && child.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(suffix => child.Name[..^suffix.Length])
                .FirstOrDefault(),
            _ => null,
        };

        return plugin is not null
            && PulumiPlugin.Recognise(plugin) is not null
            && !LongPath.FileMayExist(Path.Combine(cache, plugin + PulumiPlugin.PartialSuffix));
    }

    private static string PluginsIn(string home) => Path.Combine(home, PluginsFolder);

    private bool PulumiIsRunning() => Inspector.FindRunning(PulumiPluginUse.PulumiProcessNames).Count > 0;

    /// <summary>
    /// Every Pulumi folder in use: the one the command Deguffer starts reads, the one a Pulumi the user
    /// starts next reads, and the default.
    /// </summary>
    private IEnumerable<string> DeclaredHomes =>
        Readings()
            .Select(setting => setting.Folder)
            .Append(DefaultHome)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ConfiguredFolder.Setting> Readings() =>
        [Resolve(VariableReading.Tool), Resolve(VariableReading.Current)];

    private ConfiguredFolder.Setting Resolve(VariableReading reading) =>
        ConfiguredFolder.FromVariable(reading, HomeVariable, DefaultHome, Environment, _system, Volumes);
}
