using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The old builds CapCut and Jianying Pro leave in their <c>Apps</c> folder each time they update.
/// Users report 21 builds and 31.5 GB in one folder, and 50 GB back from removing them.
///
/// <para><b>These are not a rollback copy.</b> The launcher starts the one build its record names, and
/// the updater writes each new build beside the old ones and never removes them. Nothing starts an old
/// build unless a person edits the record, which is what the version-lock tools users run to avoid an
/// update do. Their builds are kept: the build the record names is kept, whichever it is. See
/// <see cref="CapCutBuilds"/> for which two builds survive and why.</para>
///
/// <para><b>Tier 2, and researched.</b> A build does not refill: CapCut offers no download of an old
/// one. Nobody here has seen an installation (<see cref="ProviderBasis"/>), so the row is offered and
/// never ticked, and every build is left alone wherever the record does not say plainly which one
/// starts.</para>
///
/// <para><b>An editor that is running is refused outright (§5.3)</b>, by anything running from its
/// <c>Apps</c> folder: the editor, its helpers and its background service all run from a build, and an
/// update may still be reading the folder beside the one it started from. The clean asks again before
/// each build goes.</para>
/// </summary>
public sealed class CapCutSupersededBuildProvider : CleanupProviderBase
{
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<IReadOnlyList<CapCutBuilds>> _installations = new();

    public CapCutSupersededBuildProvider(
        IUserEnvironment? environment = null,
        ILiveTreeInspector? liveTrees = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "capcut-superseded-builds";

    public override string Name => "CapCut superseded builds";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>Each step is one old build, which the user may want to keep.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "CapCut starts the build it starts now. What you give up is each older build: CapCut offers no "
        + "way to download one again. Your drafts, your settings and CapCut's caches are elsewhere and "
        + "are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.InstallersAndUpdates,
        Application = "CapCut, a free video editor, and Jianying Pro, its edition for mainland China",
        Publisher = "ByteDance",
        Purpose = "CapCut installs each update as a complete new copy of the program beside the old ones "
            + "and starts the one its launcher names. The old copies are never removed, so each update "
            + "leaves another behind.",
        Recommendation = "CapCut does not use these builds. Deguffer keeps the build CapCut starts and the "
            + "newest build, and offers the rest. They are not cleaned for you, because a build is not a "
            + "cache: once one is gone there is no way to get that version back.",
    };

    private IReadOnlyList<CapCutBuilds> Installations => _installations.Get(() =>
        [.. CapCutLayout.Editions.Select(edition => CapCutBuilds.Look(Environment, edition)).OfType<CapCutBuilds>()]);

    /// <summary>
    /// §5.2 for Explore: each <c>Apps</c> folder, where the superseded builds may go and nothing else
    /// may. Without it, Explore would let somebody delete the build CapCut starts, or its launcher, out
    /// of the size picture.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Installations.Select(installation =>
        {
            var superseded = new HashSet<string>(
                installation.Superseded.Where(build => !build.IsLink).Select(build => build.Name),
                StringComparer.OrdinalIgnoreCase);

            return ToolRoot.Folders(
                installation.Apps,
                $"This is where {installation.Edition.Name} is installed. The build it starts, its launcher "
                + "and its record of which build to start are in here, and Deguffer removes none of them.",
                superseded.Contains);
        }),
    ];

    /// <summary>The superseded builds of an editor that is running, each as a root recognising no child.</summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var withBuilds = Installations.Where(i => i.Superseded.Count > 0).ToList();
        var live = Veto(withBuilds, ct);
        var running = new HashSet<string>(live.Vetoed.Select(v => v.Directory), StringComparer.OrdinalIgnoreCase);
        var unsure = new HashSet<string>(live.Unchecked, StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. from installation in withBuilds
               where running.Contains(installation.Apps) || unsure.Contains(installation.Apps)
               let reason = running.Contains(installation.Apps) ? RunningReason(installation) : UncheckedReason(installation)
               from build in installation.Superseded
               where !build.IsLink
               select new ToolRoot(build.Path, reason, static _ => false),
        ]);
    }

    public override void InvalidateCaches()
    {
        _installations.Forget();
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>Every build, the one in use included, because the next update supersedes it.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
            [.. Installations.SelectMany(i => i.Builds).Select(build => CleanedPlace.Whole(build.Path))]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Installations.Count > 0);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var installations = Installations;
        var notes = new List<PlanNote>();
        var survivors = new List<(string Path, string Reason)>();

        // Only an installation with more than one build is withheld by a doubt. One build is nothing to
        // offer whatever its state, and a sentence about leaving it alone would mark the row unexamined.
        var doubted = installations
            .Where(i => i.Doubt is not CapCutBuildDoubt.None && (i.Builds.Count > 1 || i.Doubt is CapCutBuildDoubt.AppsUnreadable))
            .ToList();

        foreach (var installation in doubted)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, WhyLeftAlone(installation)));
        }

        var withBuilds = installations.Where(i => i.Superseded.Count > 0).ToList();
        var live = Veto(withBuilds, ct);
        var held = new HashSet<string>(live.Held, StringComparer.OrdinalIgnoreCase);
        var stillUnused = live.Cleared.ToDictionary(c => c.Path, c => c.StillUnused, StringComparer.OrdinalIgnoreCase);

        foreach (var installation in withBuilds.Where(i => held.Contains(i.Apps)))
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the older builds of {installation.Edition.Name} alone: "
                + (live.Unchecked.Contains(installation.Apps, StringComparer.OrdinalIgnoreCase)
                    ? "Deguffer could not check whether it is running. Scan again to include them."
                    : $"it is running. Close it and scan again to include them.")));
        }

        // A build somebody moved onto another drive is named and not followed: what it points at was
        // never classified, and a figure beside it would be measured through it.
        var linked = withBuilds.Where(i => !held.Contains(i.Apps)).SelectMany(i => i.Superseded.Where(b => b.IsLink)).ToList();

        foreach (var build in linked)
        {
            notes.Add(CacheLevelWalk.Note(build.Path));
        }

        var targets =
            (from installation in withBuilds
             where !held.Contains(installation.Apps)
             from build in installation.Superseded
             where !build.IsLink
             select new DeletionTarget(
                 build.Path,
                 $"{installation.Edition.Name} {build.Name}, a build {installation.Current!.Name} replaced.",
                 DirectoryAge.Of(build.Path, ct),
                 Identity: new ItemIdentity($"{installation.Edition.FolderName}/{build.Name}", $"{installation.Edition.Name} {build.Name}"),
                 Facets: [new ItemFacet("Version", build.Name)],
                 Group: installation.Edition.Name,
                 UseCheck: stillUnused[installation.Apps]))
            .ToList();

        var removing = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var installation in installations)
        {
            ct.ThrowIfCancellationRequested();
            Preserve(installation, removing, survivors);
        }

        if (targets.Count == 0 && held.Count == 0 && doubted.Count == 0 && linked.Count == 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                installations.Count == 0
                    ? "Neither CapCut nor Jianying Pro is installed for this user."
                    : "CapCut is holding no build it does not need."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. survivors]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = installations.Any(i => i.Doubt is CapCutBuildDoubt.AppsUnreadable),
            WasNotExamined = targets.Count == 0 && (held.Count > 0 || doubted.Count > 0 || linked.Count > 0),
        };
    }

    /// <summary>
    /// One query per installation at its <c>Apps</c> folder: whatever is running, it runs from the build
    /// that superseded these, so asking about a superseded folder would answer no every time.
    /// </summary>
    private LiveTreeVetoResult Veto(IReadOnlyList<CapCutBuilds> installations, CancellationToken ct) =>
        LiveTreeVeto.Apply(
            _liveTrees,
            [.. installations.Select(i => new RecognisedBuildDirectory(i.Apps, i.Apps))],
            lockFiles: [],
            ct);

    private static string UncheckedReason(CapCutBuilds installation) =>
        $"Deguffer could not tell whether {installation.Edition.Name} is running, so it is leaving its older "
        + "builds alone: an editor that has just updated may still be reading the folder beside the one it "
        + "started from.";

    private static string RunningReason(CapCutBuilds installation) =>
        $"{installation.Edition.Name} is running, so Deguffer is leaving its older builds alone: an editor that "
        + "has just updated may still be reading the folder beside the one it started from. Close it and "
        + "these can go.";

    /// <summary>
    /// §5.6 for one installation: the <c>Apps</c> folder, the launcher, its record, and every build this
    /// run is not removing, each by name. An assertion on <c>Apps</c> alone would pass with the build
    /// CapCut starts gone.
    /// </summary>
    private static void Preserve(CapCutBuilds installation, HashSet<string> removing, List<(string Path, string Reason)> survivors)
    {
        var name = installation.Edition.Name;

        survivors.Add((installation.Apps, $"The folder {name} is installed in must survive. Only builds it has replaced are removed."));
        survivors.Add((Path.Combine(installation.Apps, installation.Edition.ProcessName + ".exe"), $"{name}'s launcher, which its shortcuts start."));
        survivors.Add((Path.Combine(installation.Apps, CapCutLaunchRecord.FileName), $"{name}'s record of which build to start."));

        survivors.AddRange(
            from build in installation.Builds
            where !removing.Contains(build.Path)
            select (build.Path,
                build == installation.Current ? $"The build of {name} its launcher starts."
                : build == installation.Newest ? $"The newest build of {name}, which its next start may use."
                : build.IsLink ? CacheLevelWalk.LinkReason
                : $"A build of {name} Deguffer is leaving in place."));
    }

    private static string WhyLeftAlone(CapCutBuilds installation)
    {
        var name = installation.Edition.Name;
        var record = new UnreadFile(
            Path.Combine(installation.Apps, CapCutLaunchRecord.FileName),
            Unreached: installation.Record.State is CapCutRecordState.Unreached);

        return installation.Doubt switch
        {
            CapCutBuildDoubt.AppsUnreadable =>
                $"Deguffer could not list '{installation.Apps}', so every build of {name} is left alone.",
            CapCutBuildDoubt.RecordNotRead when installation.Record.State is CapCutRecordState.Unreached or CapCutRecordState.Unreadable =>
                $"{record.Opening($"{name}'s record of which build to start")}, so every build of {name} is left "
                + "alone: Deguffer cannot tell which one is in use.",
            CapCutBuildDoubt.RecordNotRead =>
                $"Left every build of {name} alone: its launcher's record does not name one build to start, so "
                + "Deguffer cannot tell which one is in use.",
            CapCutBuildDoubt.RecordNamesMissingBuild =>
                $"Left every build of {name} alone: its launcher's record names build {installation.Record.Version}, "
                + "which is not there, so Deguffer cannot tell which one starts.",
            CapCutBuildDoubt.UnorderableBuild =>
                $"Left every build of {name} alone: one build's number is too large to order, so Deguffer "
                + "cannot tell which build is newest.",
            _ => throw new ArgumentOutOfRangeException(nameof(installation), installation.Doubt, "An installation in no doubt has no sentence."),
        };
    }
}
