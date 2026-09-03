using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// pnpm's content-addressable store. Researched rather than measured: no pnpm was installed on the
/// machine this was written against.
///
/// <para><b>§5.1, with a command that evicts selectively.</b> <c>pnpm store prune</c> removes only
/// the packages no project on the machine still references, so unlike npm's total eviction the
/// store keeps everything in use — and the plan must estimate what the *unreferenced* part is, not
/// what the store measures. The command takes no <c>--force</c> here deliberately: pnpm's
/// <c>prune --force</c> means "also remove alien files", directories the package manager did not
/// create, and deleting what a rule cannot name is exactly what §5.2 exists to refuse.</para>
///
/// <para><b>The size is measured link-aware, and that is the reason this provider waited.</b> pnpm
/// hard-links store files into every consuming <c>node_modules</c>, so summing file lengths counts
/// each linked copy and overstates the reclaim several-fold — §5.4's lesson in a different costume.
/// <see cref="HardLinkAwareScanner"/> sums only the files nothing outside the store links, which is
/// both what pruning can actually free and what the executor's after-measure must count for the
/// delta to mean anything. That is why this provider's default scanner is the link-aware one rather
/// than <see cref="DirectoryScanner"/>.</para>
///
/// <para><b>Tier 1, on the same reading npm ships under, and with less at stake.</b> Everything the
/// command removes is, by pnpm's own accounting, referenced by no project; a later install that
/// wants one of those versions downloads it again. The registry-withdrawal residual is npm's
/// registry-wide one and is no larger here.</para>
/// </summary>
public sealed class PnpmStoreProvider : CleanupProviderBase
{
    private string? _resolvedStore;

    public PnpmStoreProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? HardLinkAwareScanner.Default)
    {
    }

    public override string Id => "pnpm";

    public override string Name => "pnpm store";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "A later install that needs a removed package downloads it again. Projects and their "
        + "node_modules are untouched: anything a project still links stays in the store.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "pnpm, an alternative package manager for Node.js",
        Publisher = "the open-source pnpm project",
        Purpose = "pnpm keeps one copy of each package version in a global store and links it "
            + "into every project that uses it, which is what makes its installs small and fast.",
        Recommendation = "Deguffer runs pnpm's own store prune, which removes only the packages "
            + "no project on the machine still links, so nothing an installed project depends on "
            + "is taken.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["node", "pnpm"];

    /// <summary>
    /// pnpm's home when it has not been asked: where the launcher, the global installs and the
    /// per-user configuration live. <c>PNPM_HOME</c> moves it, so the variable is read first.
    /// Also what the §5.6 protected paths are built from.
    /// </summary>
    public string HomeDirectory =>
        LongPath.Configured(Environment.GetEnvironmentVariable("PNPM_HOME"))
            ?? Path.Combine(Environment.LocalAppData, "pnpm");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, and here nothing inside the root is disposable.
    /// pnpm's launcher, the packages installed with <c>pnpm add --global</c> and the per-user
    /// configuration all live in this folder, and so does the store on a default install — which
    /// <c>pnpm store prune</c> works inside rather than removing, so it is a survivor too.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            HomeDirectory,
            "This is pnpm's own folder. Deguffer removes nothing inside it from here, because pnpm "
            + "itself, the packages you installed globally and the store every project on this "
            + "machine links into all live in there. Pruning the store is offered on the Storage "
            + "page, where pnpm's own command decides what no project still uses.",
            static _ => false),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("pnpm") is not null);

    /// <summary>
    /// The store moves with the <c>store-dir</c> setting and with the drive the answer was asked
    /// from, so a remembered location would measure a store pnpm has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedStore = null;
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var pnpm = Environment.FindExecutable("pnpm");
        if (pnpm is null)
        {
            return EmptyPlan("pnpm is not installed on this machine.");
        }

        var store = await ResolveStoreAsync(pnpm, ct).ConfigureAwait(false);
        if (store is null)
        {
            // No documented default to fall back on: the store directory carries a layout version
            // in its name (v3, v10, …) that moves between pnpm releases, so a guessed path would
            // either miss the store or measure a superseded one beside it.
            return EmptyPlan("pnpm did not say where its store is, so nothing is offered rather than guessed.");
        }

        if (!LongPath.DirectoryExists(store))
        {
            return EmptyPlan($"pnpm is installed but its store does not exist yet ({store}).");
        }

        var measured = await MeasureAllAsync([store], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"pnpm reports its store as {store}. Files hard-linked into a project's node_modules "
                + "are neither counted nor removed, so the figure is the store content no project "
                + "still uses. pnpm also expires its own dlx cache as part of the same command."),
            new(PlanNoteSeverity.Information,
                "pnpm keeps a separate store per drive. This plan covers the store pnpm reports for "
                + "your profile; a project on another drive uses that drive's own store."),
        };

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
            Steps =
            [
                new RunCommandStep(pnpm, "store prune", "Remove packages no project uses, using pnpm's own command")
                {
                    Estimated = measured.Total,
                    MeasuredPaths = [store],
                },
            ],
            ProtectedPaths = BuildProtectedPaths(store),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The store itself heads the list, and that is the assertion this provider most needs:
    /// <c>pnpm store prune</c> works <em>inside</em> the store, so a command that removed the whole
    /// directory would lose every package on the machine while every other check here still
    /// passed. Its neighbours matter too, and none of them is cache — the directory holding the
    /// store is pnpm's own layout, and the home carries the launcher the user runs, the packages
    /// installed with <c>pnpm add --global</c>, and the per-user configuration.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(string store) => Protect(
        (store, "The store directory itself must survive — only unreferenced packages inside it are removed."),
        (Path.GetDirectoryName(store)!, "The directory holding the store, which is pnpm's own layout."),
        (HomeDirectory, "pnpm's home directory, which holds pnpm itself and its configuration."),
        (Path.Combine(HomeDirectory, "global"), "Globally installed packages — never a cache."));

    /// <summary>
    /// Ask pnpm where the active store is. The one line of stdout is a configured value in §5.2's
    /// sense — <c>store-dir</c> moves it — so it goes through <see cref="LongPath.Configured"/>,
    /// and an answer with no containing directory (a volume root, which no pnpm writes) is treated
    /// as no answer at all rather than measured.
    /// </summary>
    private async Task<string?> ResolveStoreAsync(string pnpm, CancellationToken ct)
    {
        if (_resolvedStore is not null)
        {
            return _resolvedStore;
        }

        var outcome = await Runner.RunAsync(pnpm, "store path", ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return null;
        }

        var reported = outcome.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var store = LongPath.Configured(reported);

        return _resolvedStore = store is not null && Path.GetDirectoryName(store) is not null
            ? store
            : null;
    }
}
