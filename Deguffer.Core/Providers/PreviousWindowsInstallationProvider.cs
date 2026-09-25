using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Windows upgrade leaves at the top of the system drive once it has finished: the previous
/// installation in <c>Windows.old</c>, Setup's working folder <c>$Windows.~BT</c>, and the
/// installation files it downloaded into <c>$Windows.~WS</c> and <c>ESD</c>. <c>Windows.old</c> alone is
/// routinely tens of gigabytes, and on a recently upgraded machine it is the largest reclaim there is.
///
/// <para><b>§5.1: Windows' own cleanup, never the path.</b> Each of these has a Disk Cleanup handler,
/// and the handler is the route. <em>Previous Installations</em> also takes down the record that lets
/// Settings offer to go back, and a tree removed by path leaves that record naming a folder that is
/// gone. See <see cref="DiskCleanupStep"/>.</para>
///
/// <para><b>Tier 2.</b> None of it comes back except by upgrading again, and what deleting it costs is
/// a named capability rather than a slower next use: going back to the previous version, and — by
/// Windows' own description of the installation files — resetting this PC from them.</para>
///
/// <para><b>Offered only once the upgrade can no longer be undone.</b> Windows keeps all of this for
/// the uninstall window, ten days unless somebody has changed it, and removes it by itself at the end.
/// Offering it inside that window would take away the way back for the sake of a few days' space. So a
/// folder is offered once nothing in it has been written for longer than the window, and one Windows
/// failed to remove when the window closed is exactly what this is for.</para>
///
/// <para><b>Held back while an update is unfinished</b>, for the reasons
/// <see cref="UnfinishedUpdate"/> gives. Setup's own handler refuses while Setup is running as well;
/// this is Deguffer not offering what it would then refuse.</para>
///
/// <para><b>Offered only where the handler says it has something to clear.</b> A folder can hold
/// files the handler does not count as its own, and then Windows' cleanup declines it: see
/// <see cref="IDiskCleanupHandlers.Survey"/>. Unelevated, the handler cannot be asked, so the row is
/// offered as needing administrator rights and the plan made after elevating asks.</para>
/// </summary>
public sealed class PreviousWindowsInstallationProvider : CleanupProviderBase
{
    /// <summary>
    /// The handlers, and the directories each one's registration names, in the order a row is named
    /// after them. The handler's registration is Windows' statement of what it clears, and this table
    /// is that statement written down so it can be read and tested rather than discovered.
    /// </summary>
    internal static readonly IReadOnlyList<SetupCleanup> Cleanups =
    [
        new(
            "Previous Installations",
            ["Windows.old"],
            "The previous Windows installation, kept so the upgrade could be undone. Windows' own "
            + "cleanup removes it, together with the record that let Settings offer to go back."),
        new(
            "Temporary Setup Files",
            ["$Windows.~BT"],
            "Windows Setup's working folder from an upgrade that has finished."),
        new(
            "Windows ESD installation files",
            ["$Windows.~WS", Path.Combine("ESD", "Windows"), Path.Combine("ESD", "Download")],
            "The Windows installation files Setup downloaded for an upgrade that has finished."),
    ];

    private readonly ISystemDirectories _system;
    private readonly IWindowsServicing _servicing;
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public PreviousWindowsInstallationProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IWindowsServicing? servicing = null,
        IDiskCleanupHandlers? handlers = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            handlers: handlers)
    {
        _system = system ?? SystemDirectories.Current;
        _servicing = servicing ?? WindowsServicing.Current;
        _roots =
        [
            SystemDriveRoot.Holding(
                _system,
                [.. Cleanups.SelectMany(cleanup => cleanup.Directories.Select(directory =>
                    new DeclaredLocation(directory, cleanup.Reason)))]),
        ];
    }

    public override string Id => "previous-windows-installation";

    public override string Name => "Previous Windows installation";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Going back to the previous version of Windows is no longer possible, and anything an upgrade "
        + "left behind in the previous installation goes with it. Windows describes its downloaded "
        + "installation files as needed to reset this PC, so a reset afterwards needs Windows "
        + "downloaded again or installation media. Windows itself keeps working exactly as it is now.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows Setup",
        Publisher = "Microsoft",
        Purpose = "Upgrading Windows moves the previous installation aside into Windows.old, so the "
            + "upgrade can be undone, and leaves Setup's working folder and the installation files it "
            + "downloaded beside it. Windows keeps them for ten days after an upgrade unless that has "
            + "been changed, then removes them itself, and sometimes it does not.",
        Recommendation = "Offered only once the upgrade can no longer be undone and no update is "
            + "waiting to finish, and removed by Windows' own cleanup rather than by deleting the "
            + "folders. Look inside Windows.old first if anything from before the upgrade is missing.",
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
            return EmptyPlan("This drive holds nothing a finished Windows upgrade left behind.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        var held = new List<ProtectedPath>();
        var offered = new List<DiskCleanupTarget>();
        var unclaimed = false;

        var everything = UnfinishedUpdate.HoldsEverything(_servicing, Inspector);

        if (everything is not null)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, everything));
        }

        var window = TimeSpan.FromDays(_servicing.UninstallWindowDays);
        var now = DateTime.UtcNow;

        foreach (var cleanup in Cleanups)
        {
            var present = Present(cleanup, scan.Targets);

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

            if (paths.FirstOrDefault(_servicing.HasPendingOperationsIn) is { } pending)
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

            // Asked last, because it is the one question that can take a while: the handler measures
            // what it would clear, and Windows.old is an entire installation.
            var volume = LongPath.Display(_system.SystemDrive);
            var survey = await Task.Run(() => Handlers.Survey(cleanup.Handler, volume, ct), ct).ConfigureAwait(false);

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

        var (steps, measured) = await PlanDiskCleanupsAsync(offered, keep, ct).ConfigureAwait(false);

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
    private static string TooRecent(string path, DateTime? written, DateTime now, TimeSpan window)
    {
        var display = LongPath.Display(path);

        if (written is not { } when)
        {
            return $"Leaving {display} alone: Deguffer could not tell when it was last written, so it "
                + "cannot tell whether the upgrade can still be undone.";
        }

        var offeredFrom = when + window;
        var days = Math.Max(1, (int)Math.Ceiling((offeredFrom - now).TotalDays));

        return $"Leaving {display} alone for another {days} day{(days == 1 ? string.Empty : "s")}: Windows "
            + $"keeps it for {window.Days} days after an upgrade so the upgrade can be undone, and removes "
            + "it by itself when that time is up. Deguffer offers it if Windows has not.";
    }

    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);

    /// <summary>One Disk Cleanup handler and the directories its registration names.</summary>
    /// <param name="Handler">Its name under <c>VolumeCaches</c>.</param>
    /// <param name="Directories">Relative to the top of the system drive, the row's name first.</param>
    /// <param name="Reason">What the directories are, written for the user.</param>
    internal sealed record SetupCleanup(string Handler, IReadOnlyList<string> Directories, string Reason);
}
