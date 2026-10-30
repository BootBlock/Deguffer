using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The box versions Vagrant itself reports as superseded: every installed version of a box older
/// than the newest one for the same provider. Researched rather than measured, because Vagrant was
/// not installed on the machine this was written against; every fact below is from Vagrant's own
/// source, hashicorp/vagrant at <c>dc55920a284544919340b975098b4ac9005b08b0</c>.
///
/// <para><b>§5.1: Vagrant's own <c>box prune</c> decides and removes.</b> Its dry run names what it
/// would keep and remove, and the step is <c>box prune --force --keep-active-boxes</c>. A box is a
/// whole virtual disk image, often gigabytes, and which version is newest is Vagrant's comparison of
/// RubyGems versions, so nothing here ranks versions itself.</para>
///
/// <para><b>A version a machine was created from is never offered, and Vagrant's own flag is not
/// what ensures it.</b> <c>--keep-active-boxes</c> keeps a box only where <c>Box#in_use?</c> finds a
/// machine using it, and at this source that check compares an architecture recorded as text with
/// one held as a Ruby symbol, so it finds no machine on any box installed with an architecture (see
/// <see cref="VagrantMachineIndexReading.CreatedFrom"/>). That is not a cosmetic gap: a Hyper-V
/// machine made as a linked clone keeps its disk as a differencing disk whose parent is the image in
/// the box's own folder (<c>plugins/providers/hyperv/action/import.rb</c>), so removing that version
/// leaves the machine unable to start. So the machine index is read here, and the whole row is
/// withheld while any machine records a version prune would remove. Prune ranks the versions afresh
/// when it runs, so both are asked again immediately before it does (<see cref="VagrantPruneTarget"/>).
/// The flag stays on the command for the older index entries it does recognise.</para>
///
/// <para><b>Running Vagrant does more than read.</b> Deguffer cannot set an environment variable for a
/// tool (<see cref="IProcessRunner"/> takes none), so every run, the dry run at each scan included,
/// loads <c>Vagrantfile</c> in Vagrant's home and any <c>Vagrantfile</c> in the folder tools run in
/// or a folder above it, such as the profile; asks HashiCorp whether a newer Vagrant exists unless
/// <c>VAGRANT_CHECKPOINT_DISABLE</c> is set; and creates any of the home's own folders that are
/// missing (<c>Environment#setup_home_path</c>). Running the dry run while planning follows
/// <see cref="PlatformIoCacheProvider"/>: the dry run never reaches the remove action, and what it
/// does besides is what every Vagrant command does. What it would create is why Vagrant is never
/// started for a home with no <c>boxes</c> folder.</para>
/// </summary>
public sealed class VagrantSupersededBoxProvider : CleanupProviderBase
{
    /// <summary>Moves Vagrant's whole home (<c>Vagrant.user_data_path</c>, <c>lib/vagrant/shared_helpers.rb</c>).</summary>
    public const string HomeVariable = "VAGRANT_HOME";

    /// <summary>
    /// Prune's report of what it would do, which never calls the remove action. Without
    /// <c>--force</c>, so that were <c>--dry-run</c> ever lost from this string the command would stop
    /// at its first confirmation rather than remove anything.
    /// </summary>
    internal const string DryRun = "box prune --dry-run";

    /// <summary>
    /// <c>--force</c> answers the confirmation the remove action asks for a box it finds in use, which
    /// with standard input closed would read end of input as no; <c>--keep-active-boxes</c> then skips
    /// that box instead (<c>lib/vagrant/action/builtin/box_remove.rb</c>).
    /// </summary>
    internal const string Prune = "box prune --force --keep-active-boxes";

    private const string DeclinedRootReason =
        "VAGRANT_HOME names this folder, so Vagrant keeps its boxes, its plugins, its machine index and "
        + "its keys here.";

    /// <summary>
    /// What Vagrant keeps in its home besides the boxes, each of which must outlive a prune (§5.6).
    /// </summary>
    private static readonly (string Name, string Reason)[] HomeEntries =
    [
        ("Vagrantfile", "Vagrant's own configuration for every machine on this account."),
        (Path.Combine("data", "machine-index"), "Vagrant's record of every machine it created, and where each one's project is."),
        ("insecure_private_key", "The key Vagrant gives a new machine until it replaces it."),
        ("insecure_private_keys", "The keys Vagrant gives a new machine until it replaces them."),
        ("plugins.json", "The list of Vagrant plugins installed for this account."),
        ("gems", "The Vagrant plugins installed for this account."),
        ("aliases", "Command aliases written for this account."),
    ];

    /// <summary>
    /// Everything <c>Environment#setup_home_path</c> creates in a home, and what else Vagrant keeps at
    /// its top level.
    /// </summary>
    private static readonly string[] NamesInTheHome =
    [
        "boxes", "data", "gems", "rgloader", "tmp", "setup_version",
        .. HomeEntries.Select(entry => entry.Name.Split(Path.DirectorySeparatorChar)[0]).Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private readonly ISystemDirectories _system;

    public VagrantSupersededBoxProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "vagrant-boxes";

    public override string Name => "Vagrant superseded box versions";

    /// <summary>Tier 2: a removed version is a download again, and boxes are whole disk images.</summary>
    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "A Vagrantfile that asks for one of the removed versions downloads it again the next time a "
        + "machine is created from it. The newest version of every box stays, and no machine Vagrant "
        + "records was created from a version that is removed.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Vagrant, which builds development virtual machines from boxes",
        Publisher = "HashiCorp, an IBM company",
        Purpose = "Vagrant keeps every version of a box it has downloaded, each a whole virtual disk "
            + "image, so updating a box leaves the old version beside the new one.",
        Recommendation = "Deguffer runs Vagrant's own prune, which keeps the newest version of every "
            + "box, and offers nothing while a machine Vagrant records was made from a version prune "
            + "would remove.",
    };

    /// <summary>
    /// <c>vagrant.exe</c> is a launcher that starts the Ruby Vagrant runs on and waits for it to exit
    /// (vagrant-installers, <c>substrate/launcher/main.go</c>), so it is running whenever Vagrant is.
    /// <c>ruby</c> is not named, because every other Ruby program would then warn here.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["vagrant"];

    /// <summary>Vagrant's home where <see cref="HomeVariable"/> is unset: <c>"#{ENV["USERPROFILE"]}/.vagrant.d"</c>.</summary>
    public string DefaultHome => Path.Combine(Environment.UserProfile, ".vagrant.d");

    /// <summary>
    /// §5.2: nothing in Vagrant's home is a path target. The prune is the only route, so each home
    /// recognises no child, and Explore refuses all of it, the boxes included.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. new[] { Resolve().Folder, DefaultHome }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(home => new ToolRoot(
                home,
                "This is Vagrant's own folder. Deguffer removes a box version only by running Vagrant's "
                + "own prune, because a box is removed whole by the tool that knows which version is newest.",
                static _ => false)),
    ];

    /// <summary>
    /// A home the variable names but this declines is still where Vagrant writes, so Explore refuses
    /// what Vagrant keeps there and nothing else (<see cref="ToolRoot.Sparing"/>).
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            Resolve().DeclinedFolder is { } declined
                ? [ToolRoot.Sparing(declined, DeclinedRootReason, NamesInTheHome)]
                : []);

    /// <summary>The boxes of the home in use and of the default home, whichever the variable says when prune runs.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. new[] { Resolve().Folder, DefaultHome }
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(home => CleanedPlace.Whole(BoxesIn(home))),
        ]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("vagrant") is not null);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("vagrant") is not { } vagrant)
        {
            return EmptyPlan("Vagrant is not installed on this machine.");
        }

        var setting = Resolve();

        if (setting.Folder is not { } home)
        {
            return UnexaminedPlan(
                $"{HomeVariable} is set to '{setting.Value}', and Deguffer will not treat that as Vagrant's "
                + $"folder: {setting.Declined} Vagrant is not run, and none of its boxes is offered.");
        }

        var boxes = BoxesIn(home);

        if (NothingToPlanFor(boxes, $"Vagrant has no boxes for this account: {boxes} does not exist.") is { } nothing)
        {
            return nothing;
        }

        // Read before the dry run rather than after it, so a box installed or removed while the dry
        // run is ranking them shows at the clean as a change, never as part of a layout the dry run
        // did not see. See VagrantPruneTarget.
        if (VagrantBoxLayout.Installed(boxes) is not { } installed)
        {
            var unread = new PlanNote(
                PlanNoteSeverity.Warning,
                $"Deguffer could not read every box folder in {boxes}, so it could not check again when the "
                + "clean runs that Vagrant's prune would remove only what it shows, and offers nothing.");

            return UnexaminedPlan(unread.Message) with { Notes = [unread] };
        }

        var outcome = await Runner.RunAsync(vagrant, DryRun, ToolRun.Query, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return UnexaminedPlan(
                "Vagrant could not say which box versions its prune would remove, so none is offered. "
                + $"It said: {outcome.Message}");
        }

        if (VagrantPruneReport.TryRead(outcome.StandardOutput) is not { } preview)
        {
            return UnexaminedPlan(
                "Vagrant's prune did not report which box versions it would keep and remove, so none is "
                + "offered. Measuring the boxes instead would count the newest version of each.");
        }

        if (preview.Removed.Count == 0)
        {
            return EmptyPlan("Vagrant reports no superseded box versions: every installed box is at its newest version.");
        }

        var located = preview.Removed
            .Distinct()
            .Select(box => (Box: box, Folders: VagrantBoxLayout.ProviderDirectories(boxes, box, IsThere)))
            .ToList();

        if (VagrantPrunePlanNotes.Blockers(preview, located, VagrantMachineIndex.Read(home)) is { Count: > 0 } blockers)
        {
            return UnexaminedPlan(blockers[0].Message) with { Notes = blockers };
        }

        IReadOnlyList<string> measuredPaths = [.. located.SelectMany(found => found.Folders)];

        if (measuredPaths.Count == 0)
        {
            return UnexaminedPlan(
                $"Vagrant named superseded box versions, and none of them is in {boxes} where Vagrant keeps "
                + "a box, so nothing is offered.");
        }

        var measured = await MeasureAllAsync(measuredPaths, ct).ConfigureAwait(false);

        List<PlanNote> notes = [.. VagrantPrunePlanNotes.Evidence(preview, located), VagrantPrunePlanNotes.SideEffects];

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } running)
        {
            notes.Add(running);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(vagrant, Prune, "Remove superseded box versions using Vagrant's own prune")
                {
                    // Box#destroy! is FileUtils.rm_r of the provider folder, which removes entries in
                    // the order Windows lists them, so metadata.json can outlive the disk image beside
                    // it. Vagrant takes any folder holding metadata.json as an installed box, so a
                    // prune ended part-way could leave a broken box that a machine is then built from.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total,
                    MeasuredPaths = measuredPaths,
                    TargetCheck = new VagrantPruneTarget(home, installed, [.. located.Select(found => found.Box)]),
                },
            ],
            ProtectedPaths = BuildProtectedPaths(home, boxes, preview, located),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The home and what Vagrant keeps in it, every box folder, and every version folder of a
    /// box prune touches that it is not removing: the newest of each, kept by prune itself, and any
    /// folder prune never names. The newest version's provider folders are named as well, because
    /// they sit beside what is removed and an over-broad removal would take them first, and so is
    /// whatever shares a removed version's folder without being removed with it.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        string home,
        string boxes,
        VagrantPrunePreview preview,
        IReadOnlyList<(VagrantBoxVersion Box, IReadOnlyList<string> Folders)> located)
    {
        var removedByName = preview.Removed
            .GroupBy(box => box.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlySet<string>)group.Select(box => box.Version).ToHashSet(StringComparer.OrdinalIgnoreCase));

        IReadOnlyList<string> removing = [.. located.SelectMany(found => found.Folders)];
        var listing = ChildDirectories.Under(boxes);

        return Protect(
        [
            (home, "Vagrant's own folder must survive. Only superseded box versions inside it are removed."),
            (boxes, "Every installed box, the newest version of each included."),
            .. HomeEntries.Select(entry => (Path.Combine(home, entry.Name), entry.Reason)),
            .. listing.Directories.Concat(listing.Links).Select(box => (LongPath.Display(box.FullName),
                "A box keeps its newest version, so its folder survives a prune.")),
            .. preview.Kept.SelectMany(box => VagrantBoxLayout.ProviderDirectories(boxes, box, MayBeThere))
                .Select(folder => (folder, "The newest version of a box, which prune keeps.")),
            .. removedByName.SelectMany(pair => VagrantBoxLayout.OtherVersionDirectories(boxes, pair.Key, pair.Value))
                .Select(folder => (folder, "A version folder prune does not remove.")),
            .. located.SelectMany(found => VagrantBoxLayout.Beside(boxes, found.Box, removing))
                .Select(folder => (folder, "Beside a version prune removes, and not part of it.")),
        ]);
    }

    private static string BoxesIn(string home) => Path.Combine(home, "boxes");

    private static bool IsThere(PathPresence presence) => presence is PathPresence.Present;

    private static bool MayBeThere(PathPresence presence) => presence is not PathPresence.Absent;

    private ConfiguredFolder.Setting Resolve() =>
        ConfiguredFolder.FromVariable(HomeVariable, DefaultHome, Environment, _system, Volumes);
}
