using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Folders Windows' update machinery leaves at the top of the system drive and never clears:
/// <c>$WinREAgent</c>, where servicing the recovery environment stages its images and its rollback
/// state (1.7 GB on the audited machine, from the most recent cumulative update), and
/// <c>$GetCurrent</c>, the update assistant's working folder.
///
/// <para><b>This is Deguffer's judgement, not Microsoft's, and every word the user sees says so.</b>
/// No Disk Cleanup handler names either folder — every registration's full property set was
/// searched — and Microsoft documents neither. Every disk cleaner deletes them, and nothing
/// first-party says that is safe. So they are offered on terms that make the judgement a narrow
/// one: nothing inside has been created or written for <see cref="QuietDays"/> days, no update is
/// waiting for a restart or being installed, and no restart is due to change anything inside.</para>
///
/// <para><b>Why thirty days.</b> The rollback state in <c>$WinREAgent</c> is stale only once the
/// servicing operation that wrote it has completed, and that cannot be asked of Windows without
/// administrator rights: <c>reagentc /info</c> refuses, and <c>C:\Recovery</c> refuses to be listed.
/// A month is several times the length of any servicing operation and short of the next month's
/// update, and the nine-day-old copy on the audited machine is exactly what it keeps back.</para>
///
/// <para><b>Each folder goes whole or not at all.</b> A rollback manifest is meaningless without the
/// image it restores, so a folder with anything recent inside it is withheld whole rather than
/// having its older half removed, and a removal step never meets a newer file by surprise.</para>
///
/// <para><c>$SysReset</c> is not here, though it is the third of the family. Windows' own
/// <em>System recovery log files</em> cleanup names its logs, so §5.1 sends them through that, and
/// they are logs, so they are Tier 3 with the rest — see <see cref="WindowsServicingLogProvider"/>.
/// </para>
/// </summary>
public sealed class WindowsUpdateLeftoverProvider : CleanupProviderBase
{
    /// <summary>How long nothing inside a folder must have changed before it is offered.</summary>
    public const int QuietDays = 30;

    private readonly IWindowsServicing _servicing;
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public WindowsUpdateLeftoverProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IWindowsServicing? servicing = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _servicing = servicing ?? WindowsServicing.Current;
        _roots =
        [
            SystemVolumeRoot.Holding(
                system ?? SystemDirectories.Current,
                new DeclaredLocation(
                    "$WinREAgent",
                    "What servicing the recovery environment staged during an update: a working image, a "
                    + "backup of the previous recovery image, and the state to roll back to."),
                new DeclaredLocation(
                    "$GetCurrent",
                    "The update assistant's working folder: its logs and the files it downloaded.")),
        ];
    }

    public override string Id => "windows-update-leftovers";

    public override string Name => "Leftover Windows update folders";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The files the last update staged for the recovery environment, and the rollback state beside "
        + "them, are gone, as are the update assistant's logs and downloads. Windows creates these "
        + "folders again the next time it needs them. Microsoft documents neither folder, so offering "
        + "them is Deguffer's judgement rather than Microsoft's.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows Update and the recovery environment",
        Publisher = "Microsoft",
        Purpose = "When an update services the recovery environment, Windows stages a new recovery "
            + "image, a backup of the old one and the state it would roll back to in $WinREAgent. The "
            + "update assistant keeps its logs and downloads in $GetCurrent. Neither is cleared "
            + "afterwards, and $WinREAgent alone is often close to two gigabytes.",
        Recommendation = "Microsoft does not document either folder, and no Windows cleanup names "
            + $"them. Deguffer offers one only once nothing inside has changed for {QuietDays} days and "
            + "no update is waiting to finish.",
    };

    /// <summary>What this provider names. Exposed so tests can assert the declaration itself.</summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryMayExist));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan("This drive holds none of the folders Windows updates leave behind.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        var held = new List<ProtectedPath>();

        // Fixed once, here, so the preview and the removal agree about which files are recent however
        // long the preview sits on screen — the reason TempDirectoryProvider fixes its floor.
        var floor = MinimumAge.Within(TimeSpan.FromDays(QuietDays), DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var candidates = new List<DeletionTarget>(scan.Targets.Count);

        if (UnfinishedUpdate.HoldsEverything(_servicing, Inspector) is { } everything)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, everything));
            held.AddRange(scan.Targets.Select(t => UnfinishedUpdate.Held(t.Path)));
        }
        else
        {
            foreach (var target in scan.Targets)
            {
                if (_servicing.HasPendingOperationsIn(target.Path))
                {
                    notes.Add(new PlanNote(PlanNoteSeverity.Information, UnfinishedUpdate.PendingIn(target.Path)));
                    held.Add(UnfinishedUpdate.Held(target.Path));
                }
                else
                {
                    candidates.Add(target);
                }
            }
        }

        var (planned, measured) = await PlanDeletionsAsync(candidates, effective, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(planned.Count);

        foreach (var step in planned.Cast<DeleteDirectoryStep>())
        {
            if (!step.WithheldRecent)
            {
                steps.Add(step with { IsIndivisible = true });
                continue;
            }

            // Whole, as the class comment says: something recent inside means the servicing that wrote
            // it may still need the rest.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {LongPath.Display(step.Path)} alone: something inside it changed in the last "
                + $"{effective.Describe()}, so the update that wrote it may not have finished with it."));
            held.Add(new ProtectedPath(
                step.Path,
                $"Left alone because something inside it changed in the last {effective.Describe()}.",
                PathPresence.Present,
                Withheld: Withholding.TooRecent));
        }

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            "Microsoft documents none of these folders and no Windows cleanup names them, so offering "
            + $"them is Deguffer's judgement: only once nothing inside has changed for {floor.Describe()} "
            + "and no update is waiting to finish."));

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
            ProtectedPaths = [.. Protect([.. scan.Protected]), .. held],
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = scan.NothingWasExamined,
            HasUnreadableRoot = scan.CouldNotBeReached,

            // The floor travels on the plan, so a file written between the preview and the clean is
            // left where it is rather than taken. CleanupProviderBase.PlanAsync will not loosen it.
            Keep = floor,
        };
    }

    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
