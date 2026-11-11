using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Windows Setup leaves at the top of the system drive, removed through the Disk Cleanup handlers
/// <c>setupcln.dll</c> serves. One class serves every one of those handlers and tells them apart by the
/// values on each registration's key (see <see cref="DiskCleanupHandlers"/>), so each row here is one
/// registration and the directories it names. A provider says which registrations are its own, and
/// what losing them costs.
///
/// <para><b>§5.1: Windows' own cleanup, never the path.</b> Each of these has a Disk Cleanup handler,
/// and the handler is the route. The <em>Previous Installations</em> registration asks its handler to
/// remove the uninstall record too (<c>RemoveUninstall</c>), and a tree removed by path would leave
/// that record naming a folder that is gone. See <see cref="DiskCleanupStep"/>.</para>
///
/// <para><b>Offered only once the upgrade can no longer be undone.</b> Windows keeps what an upgrade
/// leaves for the uninstall window, ten days unless somebody has changed it. Offering any of it inside
/// that window would take away the way back, or the chance to look for what the upgrade did not carry
/// over, for the sake of a few days' space. So a folder is offered once neither it nor anything
/// directly inside it has been written for longer than the window (see <see cref="DirectoryAge"/>).</para>
///
/// <para><b>Held back while an update is unfinished</b>, for the reasons
/// <see cref="UnfinishedUpdate"/> gives. Setup's own handler refuses while Setup is running as well;
/// this is Deguffer not offering what it would then refuse.</para>
///
/// <para><b>Offered only where the handler says it has something to clear.</b> A folder can hold
/// files the handler does not count as its own, and then Windows' cleanup declines it: see
/// <see cref="IDiskCleanupHandlers.Survey"/>. The handler is asked only whether it starts
/// (<see cref="DiskCleanupQuestion.Starts"/>). Unelevated, the handler cannot be asked, so the row is
/// offered as needing administrator rights and the plan made after elevating asks.</para>
/// </summary>
public abstract class SetupCleanupProviderBase : CleanupProviderBase
{
    /// <summary>
    /// Asked whether Windows' own cleanup has anything to clear, and given to the executor that runs it,
    /// so the plan and the clean ask the same instance. See <see cref="CleanupProviderBase"/>'s
    /// <c>handlers</c>.
    /// </summary>
    private readonly IDiskCleanupHandlers _handlers;

    private readonly ISystemDirectories _system;
    private readonly IReadOnlyList<SetupCleanup> _cleanups;
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    /// <param name="cleanups">
    /// The registrations this provider clears through, in the order its rows are named after them.
    /// </param>
    protected SetupCleanupProviderBase(
        IReadOnlyList<SetupCleanup> cleanups,
        IDiskCleanupHandlers handlers,
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner,
        ISystemDirectories? system,
        IWindowsServicing? servicing)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            handlers: handlers,
            servicing: servicing)
    {
        _cleanups = cleanups;
        _handlers = handlers;
        _system = system ?? SystemDirectories.Current;
        _roots =
        [
            SystemDriveRoot.Holding(
                _system,
                [.. cleanups.SelectMany(cleanup => cleanup.Directories.Select(directory =>
                    new DeclaredLocation(directory, cleanup.Reason)))]),
        ];
    }

    public override StepGrain Grain => StepGrain.Parts;

    /// <summary>What this provider names. Exposed so tests can assert the declaration itself.</summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>The plan's sentence for a drive holding none of this provider's directories.</summary>
    protected abstract string NothingLeftBehind { get; }

    /// <summary>
    /// Why a folder is still held back inside the uninstall window, as the clause that follows "Leaving
    /// it alone for another few days:".
    /// </summary>
    /// <param name="windowDays">The uninstall window this machine is set to, in days.</param>
    protected abstract string WhileTheUpgradeCanBeUndone(int windowDays);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryMayExist));

    /// <summary>
    /// Each folder Setup left on the system drive, whole: Windows' own cleanup clears each of them
    /// entirely, and nothing inside one is told apart from the rest of it.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. DeclaredPaths().Select(CleanedPlace.Whole)]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan(NothingLeftBehind);
        }

        // Read before the loop, because each cleanup is offered only once its folders are past the
        // window, and a declared folder's age is read with the rest of the scan's in one step.
        var targets = await DatedAsync(scan.Targets, ct).ConfigureAwait(false);

        var notes = new List<PlanNote>(scan.Notes);
        var held = new List<ProtectedPath>();
        var offered = new List<DiskCleanupTarget>();
        var unclaimed = false;

        var everything = UnfinishedUpdate.HoldsEverything(Servicing, Inspector);

        if (everything is not null)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, everything));
        }

        var window = TimeSpan.FromDays(Servicing.UninstallWindowDays);
        var now = DateTime.UtcNow;

        foreach (var cleanup in _cleanups)
        {
            var present = Present(cleanup, targets);

            if (present.Count == 0)
            {
                continue;
            }

            var paths = present.Select(t => t.Path).ToList();

            if (everything is not null)
            {
                held.AddRange(paths.Select(UnfinishedUpdate.Held));
                continue;
            }

            if (paths.FirstOrDefault(Servicing.HasPendingOperationsIn) is { } pending)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, UnfinishedUpdate.PendingIn(pending)));
                held.AddRange(paths.Select(UnfinishedUpdate.Held));
                continue;
            }

            // Every directory the handler clears has to be past the window, because it clears them
            // together. A date that could not be read is not one that has passed.
            var newest = present.Any(t => t.LastWritten is null) ? null : present.Max(t => t.LastWritten);

            if (newest is not { } written || now - written < window)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, TooRecent(paths[0], newest, now, window)));
                held.AddRange(paths.Select(path => new ProtectedPath(
                    path,
                    "Left alone because the upgrade that wrote it can still be undone.",
                    PathPresence.Present,
                    Withheld: Withholding.TooRecent)));
                continue;
            }

            // Asked last, so no folder another rule holds back is handed to Windows' handler. Asked
            // only whether it starts: setupcln.dll decides when it starts, and asked to measure,
            // Previous Installations walks the whole of Windows.old, which the plan measures anyway.
            var volume = LongPath.Display(_system.SystemDrive);
            var survey = await Task.Run(
                () => _handlers.Survey(cleanup.Handler, volume, DiskCleanupQuestion.Starts, ct), ct).ConfigureAwait(false);

            if (survey.WhyLeftAlone(cleanup.Handler, LongPath.Display(paths[0])) is { } why)
            {
                unclaimed = true;
                notes.Add(new PlanNote(PlanNoteSeverity.Information, why));
                held.AddRange(paths.Select(path => new ProtectedPath(
                    path, "Left alone because Windows' own cleanup does not clear it.", PathPresence.Present)));
                continue;
            }

            offered.Add(new DiskCleanupTarget(
                paths[0],
                cleanup.Reason,
                cleanup.Handler,
                volume,
                [.. paths.Skip(1)],
                written,
                RequiresElevation: true));
        }

        var (cleanups, measured) = await PlanDiskCleanupsAsync(offered, keep, ct).ConfigureAwait(false);

        // Asked again when the run reaches each one: an update can start while the preview is open.
        IReadOnlyList<CleanupStep> steps = [.. cleanups.Select(step => step with { HeldWhileUpdating = true })];

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
            // A folder Windows' own cleanup will not clear is one Deguffer declined to act on, and a row
            // holding nothing else must not read "Already clear" above it.
            WasNotExamined = scan.NothingWasExamined || (unclaimed && steps.Count == 0),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    /// <summary>
    /// Which of a handler's directories are there, in the handler's own order, so a row is named after
    /// the first of them.
    /// </summary>
    private IReadOnlyList<DeletionTarget> Present(SetupCleanup cleanup, IReadOnlyList<DeletionTarget> targets) =>
    [
        .. cleanup.Directories
            .Select(directory => Path.Combine(_system.SystemDrive, directory))
            .SelectMany(path => targets.Where(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase))),
    ];

    /// <summary>
    /// Why a folder the upgrade can still use is not offered, with the date that decided it where
    /// there was one.
    /// </summary>
    private string TooRecent(string path, DateTime? written, DateTime now, TimeSpan window)
    {
        var display = LongPath.Display(path);

        if (written is not { } when)
        {
            return $"Leaving {display} alone: Deguffer could not tell when it was last written, so it "
                + "cannot tell whether the upgrade can still be undone.";
        }

        var offeredFrom = when + window;
        var days = Math.Max(1, (int)Math.Ceiling((offeredFrom - now).TotalDays));

        return $"Leaving {display} alone for another {days} day{(days == 1 ? string.Empty : "s")}: "
            + WhileTheUpgradeCanBeUndone(window.Days);
    }

    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);

    /// <summary>One Disk Cleanup handler and the directories its registration names.</summary>
    /// <param name="Handler">Its name under <c>VolumeCaches</c>.</param>
    /// <param name="Directories">
    /// Relative to the top of the system drive, the row's name first: the registration's
    /// <c>SetupDirectories</c> value, where it has one.
    /// </param>
    /// <param name="Reason">What the directories are, written for the user.</param>
    protected internal sealed record SetupCleanup(string Handler, IReadOnlyList<string> Directories, string Reason);
}
