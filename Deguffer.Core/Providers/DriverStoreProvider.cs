using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Older versions of third-party drivers in Windows' driver store, <c>FileRepository</c>. Windows
/// stages every driver package it installs there and keeps each one after a newer version of the same
/// driver arrives, so a machine that has taken a few graphics, wireless or Bluetooth updates holds one
/// folder per version. 94 older folders held 1.06 GB on the machine this was measured on.
///
/// <para><b>§5.1: Windows' own cleanup decides.</b> Disk Cleanup's <em>Device driver packages</em>
/// handler is Microsoft's code judging which packages are superseded, and it is a better authority
/// than <see cref="SupersededDrivers"/>'s comparison of names and versions. So it is the route
/// wherever Windows registers it, and the plan names what Deguffer's own reading expects it to take,
/// for the figure and for what the run checks afterwards. Where the handler cannot be loaded, each older
/// package is removed with <c>pnputil /delete-driver</c>, the driver store's own command, never with
/// <c>/force</c>, which removes a package a device is using, and never with <c>/uninstall</c>, which
/// takes the driver away from the devices using it.</para>
///
/// <para><b>§5.2.</b> <c>FileRepository</c> is never a target, and neither is a package
/// <c>pnputil</c> does not list as third-party. The only children offered are older packages of a kind
/// that has a newer one beside them, and §5.6 checks afterwards that the store, the newest of each
/// kind, every package a device is using and every driver that is part of Windows are all still
/// there. Where <c>pnputil</c> does the work, Deguffer names exactly what goes, so every other
/// third-party package is checked too.</para>
///
/// <para><b>Tier 2.</b> Getting an older driver back means downloading it from the manufacturer or
/// Windows Update, and at worst a device needs its driver before the network works.</para>
///
/// <para><b>Measured link-aware</b>, for the reason <see cref="PnpmStoreProvider"/> is: a file the
/// servicing store also links is not freed by removing the package.</para>
/// </summary>
public sealed class DriverStoreProvider : CleanupProviderBase
{
    /// <summary>The Disk Cleanup handler, as Windows registers it under <c>VolumeCaches</c>.</summary>
    public const string Handler = "Device Driver Packages";

    /// <summary>
    /// Asked whether Windows' own cleanup has anything to clear, and given to the executor that runs it,
    /// so the plan and the clean ask the same instance. See <see cref="CleanupProviderBase"/>'s
    /// <c>handlers</c>.
    /// </summary>
    private readonly IDiskCleanupHandlers _handlers;

    private readonly ISystemDirectories _system;
    private readonly IDriverStore _store;
    private readonly string _repository;

    /// <summary>
    /// The listing presence and planning both read, taken once per planning pass (G4): it is a
    /// subprocess and a SetupAPI call per package.
    /// </summary>
    private readonly PassMemo<Task<DriverStoreListing>> _listing = new();

    /// <summary>
    /// Windows' cleanup asked what it would clear, started with the listing and handed to the plan,
    /// which takes it or discards it. See <see cref="ListingAsync"/>.
    /// </summary>
    private EarlyDiskCleanupSurvey? _early;

    /// <summary>The system volume, in the display form the handler answers for.</summary>
    private readonly string _volume;

    public DriverStoreProvider(
        IDiskCleanupHandlers handlers,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IWindowsServicing? servicing = null,
        IDriverStore? store = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? HardLinkAwareScanner.Default,
            handlers: handlers,
            servicing: servicing)
    {
        _handlers = handlers;
        _system = system ?? SystemDirectories.Current;
        _store = store ?? new DriverStore(Runner, _system);
        _repository = Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository");
        _volume = LongPath.Display(_system.SystemDrive);
    }

    public override string Id => "driver-store";

    public override string Name => "Superseded driver packages";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Windows can no longer roll a device back to an older version of its driver, or install one "
        + "from the copy it kept. The newest version of every driver, and every driver a device is "
        + "using, stays. A device that needs an older driver again gets it from the manufacturer or "
        + "Windows Update.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "Windows' driver store",
        Publisher = "Microsoft",
        Purpose = "Windows keeps a copy of every driver package it installs in its driver store, so it "
            + "can set a device up again without asking for the driver. Nothing removes the older copy "
            + "when a newer version of the same driver arrives, so graphics, wireless and Bluetooth "
            + "drivers in particular pile up one version at a time.",
        Recommendation = "Removed by Windows' own driver package cleanup, which decides for itself which "
            + "versions are no longer needed. The newest version of each driver and every driver a device "
            + "is using are never removed.",
    };

    public override void InvalidateCaches()
    {
        _listing.Forget();
        Interlocked.Exchange(ref _early, null)?.Abandon();
        base.InvalidateCaches();
    }

    /// <summary>
    /// A store holding an older version of some driver. A listing that failed is no evidence that
    /// nothing is older, so the row appears and its plan says what Windows answered.
    /// </summary>
    public override async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        if (!LongPath.DirectoryMayExist(_repository))
        {
            return false;
        }

        var listing = await ListingAsync(ct).ConfigureAwait(false);
        var present = listing.Failure is not null || SupersededDrivers.Of(listing.Packages, _repository).Newest.Count > 0;

        // The planner plans only a row that is present, so nothing else would take the answer.
        if (!present && TakeEarlySurvey() is { } unneeded)
        {
            await unneeded.DiscardAsync().ConfigureAwait(false);
        }

        return present;
    }

    /// <summary>
    /// The driver store's package folders, and the Windows <c>INF</c> folder, both whole. Which
    /// packages go is Windows' decision rather than this row's, and removing one also removes the
    /// <c>oem&lt;N&gt;.inf</c> copy Windows installed it under, which sits in <c>INF</c>.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            CleanedPlace.Whole(_repository),
            CleanedPlace.Whole(Path.Combine(_system.WindowsDirectory, "INF")),
        ]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(_repository, "This Windows keeps no driver store.") is { } nothing)
        {
            return nothing;
        }

        var listing = await ListingAsync(ct).ConfigureAwait(false);
        var early = TakeEarlySurvey();

        try
        {
            return await PlanOverAsync(listing, early, keep, ct).ConfigureAwait(false);
        }
        finally
        {
            // Every plan that returns before it needs Windows' answer leaves the survey unread, and
            // one that took the answer has nothing left to stop.
            if (early is not null)
            {
                await early.DiscardAsync().ConfigureAwait(false);
            }
        }
    }

    /// <param name="early">
    /// The survey started with the listing, or null where none was: an update held it back, or this is
    /// the pass's second plan over one listing. Read only once every gate has passed.
    /// </param>
    private async Task<CleanupPlan> PlanOverAsync(
        DriverStoreListing listing,
        EarlyDiskCleanupSurvey? early,
        MinimumAge keep,
        CancellationToken ct)
    {
        if (listing.Failure is { } failure)
        {
            return UnexaminedPlan(
                $"Windows did not list its driver packages: {failure} Nothing is offered rather than guessed at.");
        }

        var drivers = SupersededDrivers.Of(listing.Packages, _repository);

        if (drivers.Newest.Count == 0)
        {
            return EmptyPlan("No driver in Windows' driver store has an older version of itself beside it.");
        }

        var notes = new List<PlanNote>(DriverPackageNotes.Considered(listing, drivers));
        var unlisted = Unlisted(listing, notes);

        // A third-party package that did not read, or whose folder Windows would not name, is also a
        // folder the listing does not account for, so an unlisted folder is Windows' own only where
        // every listed package was matched to its folder.
        var matched = listing.Unread == 0 && listing.Packages.All(p => p.Folder is not null);
        var kept = Protect(
        [
            .. KeptPaths(drivers.Newest, listing),
            .. matched ? unlisted.Select(folder => (folder, "A driver that is part of Windows, which pnputil does not list as third-party.")) : [],
            .. WindowsSystemRoot.ExclusionsUnder(_system.WindowsDirectory),
        ]);

        if (drivers.Superseded.Count == 0)
        {
            return Plan([], notes, kept);
        }

        var folders = drivers.Superseded.Select(s => s.Package.Folder!).ToList();

        if (UnfinishedUpdate.HoldsEverything(Servicing, Inspector) is { } updating)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, updating));
            return Plan([], notes, [.. kept, .. folders.Select(UnfinishedUpdate.Held)]);
        }

        // Read last: every gate above decides over the answer's use, never over whether it was asked.
        var survey = await (early ?? new EarlyDiskCleanupSurvey(_handlers, Handler, _volume, ct)).TakeAsync().ConfigureAwait(false);

        return survey.Answer switch
        {
            DiskCleanupAnswer.Unavailable => await ByPackageAsync(
                drivers, listing, survey, notes, [.. kept, .. matched ? [] : Protect([.. unlisted.Select(folder => (folder, DriverPackageNotes.NotAsked))])], ct).ConfigureAwait(false),
            _ when survey.MayOffer => await ByHandlerAsync(folders, _volume, keep, matched, notes, kept, ct).ConfigureAwait(false),
            _ => Plan(
                [],
                [.. notes, new PlanNote(PlanNoteSeverity.Information, survey.WhyLeftAlone("Device driver packages", LongPath.Display(_repository))!)],
                [.. kept, .. folders.Select(folder => new ProtectedPath(
                    folder, "Left alone because Windows' own cleanup does not clear it.", PathPresence.Present))]) with
            {
                // Deguffer declined to act, so the row must not read "Already clear" over older
                // packages it can see.
                WasNotExamined = true,
            },
        };
    }

    /// <summary>
    /// Windows' own cleanup, as one step: the handler takes every package it judges superseded at once
    /// and cannot be asked to take some of them.
    /// </summary>
    private async Task<CleanupPlan> ByHandlerAsync(
        IReadOnlyList<string> folders,
        string volume,
        MinimumAge keep,
        bool matched,
        List<PlanNote> notes,
        IReadOnlyList<ProtectedPath> kept,
        CancellationToken ct)
    {
        if (!matched)
        {
            notes.Add(DriverPackageNotes.Unmatched);
        }

        var target = new DiskCleanupTarget(
            folders[0],
            "Older versions of drivers that have a newer version beside them, removed by Windows' own "
            + "driver package cleanup",
            Handler,
            volume,
            [.. folders.Skip(1)],
            Newest(await DateAsync(folders, ct).ConfigureAwait(false)),
            RequiresElevation: true);

        var (steps, measured) = await PlanDiskCleanupsAsync([target], keep, ct).ConfigureAwait(false);

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            "Windows' own cleanup decides which older driver packages to remove, and keeps the newest "
            + "version of each driver and every driver a device is using. The figure is the "
            + $"{DriverPackageNotes.Count(folders.Count, "older package")} Deguffer found with a newer version beside them, so Windows may "
            + "remove a little more or less."));

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // Asked again when the run reaches it: Windows Update installs drivers as well.
        return Plan([.. steps.Select(step => step with { HeldWhileUpdating = true })], notes, kept) with
        {
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// The driver store's own command, one package at a time, where Windows' cleanup cannot be loaded.
    /// Deguffer names exactly what goes here, so every other package it listed is checked afterwards.
    /// </summary>
    private async Task<CleanupPlan> ByPackageAsync(
        SupersededDrivers drivers,
        DriverStoreListing listing,
        DiskCleanupSurvey survey,
        List<PlanNote> notes,
        IReadOnlyList<ProtectedPath> kept,
        CancellationToken ct)
    {
        var folders = drivers.Superseded.Select(s => s.Package.Folder!).ToList();
        var dating = DateAsync(folders, ct);
        var measuring = MeasureAllAsync(folders, ct);

        await Task.WhenAll(dating, measuring).ConfigureAwait(false);

        var dates = await dating.ConfigureAwait(false);
        var measured = await measuring.ConfigureAwait(false);
        var pnpUtil = DriverStore.PnpUtil(_system);

        IReadOnlyList<CleanupStep> steps =
        [
            .. drivers.Superseded.Select((superseded, i) => new RunCommandStep(
                pnpUtil,
                $"/delete-driver {superseded.Package.PublishedName}",
                $"Remove {DriverPackageNotes.Described(superseded.Package)}, which version {superseded.Newest.Version} of "
                + $"{superseded.Newest.Date:d MMMM yyyy} replaces, using Windows' own command")
            {
                // The driver store is Windows' own, and nothing documents what a removal ended part-way
                // leaves in it, so a started removal is left to finish.
                WhenCancelled = CancelledCommand.Finishes,
                Removes = superseded.Package.Folder,
                MeasuredPaths = [superseded.Package.Folder!],
                Estimated = measured.Sizes[i],
                LastWritten = dates[i],
                RequiresElevation = true,
                HeldWhileUpdating = true,
                TargetCheck = new DriverPackageName(_store, superseded.Package.PublishedName),
            }),
        ];

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            "Windows' own driver package cleanup is not available to Deguffer here"
            + (survey.Message is { } why ? $" ({why.TrimEnd('.')})" : string.Empty)
            + ", so each older package is removed with pnputil, which refuses to remove one a device is using."));

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        var accounted = new HashSet<string>([.. folders, .. kept.Select(k => k.Path)], StringComparer.OrdinalIgnoreCase);
        var others = listing.Packages
            .Select(p => p.Folder)
            .OfType<string>()
            .Where(folder => accounted.Add(folder) && SupersededDrivers.IsChildOf(folder, _repository))
            .Select(folder => (folder, DriverPackageNotes.NotAsked));

        return Plan(steps, notes, [.. kept, .. Protect([.. others])]) with { Fallback = measured.Fallback };
    }

    private CleanupPlan Plan(IReadOnlyList<CleanupStep> steps, IReadOnlyList<PlanNote> notes, IReadOnlyList<ProtectedPath> protectedPaths) => new()
    {
        ProviderId = Id,
        ProviderName = Name,
        Tier = Tier,
        WhatHappensOnNextUse = WhatHappensOnNextUse,
        Steps = steps,
        ProtectedPaths = protectedPaths,
        Notes = notes,
    };

    /// <summary>
    /// The store itself, and the folder of every package that stays on either route: the newest of each
    /// driver that has an older version, and every package a device is using, which Windows' cleanup
    /// does not remove and <c>pnputil</c> refuses to.
    /// </summary>
    private IEnumerable<(string Path, string Reason)> KeptPaths(IReadOnlyList<DriverPackage> newest, DriverStoreListing listing)
    {
        yield return (_repository, "Windows' driver store, which holds every driver installed on this machine.");

        var kept = newest.Concat(listing.Packages.Where(p => p.InUse))
            .Where(p => p.Folder is not null)
            .DistinctBy(p => p.Folder, StringComparer.OrdinalIgnoreCase);

        foreach (var package in kept)
        {
            yield return (package.Folder!, package.InUse
                ? $"{DriverPackageNotes.Described(package)}, which a device is installed with."
                : $"{DriverPackageNotes.Described(package)}, the newest version of that driver here.");
        }
    }

    /// <summary>
    /// Every folder in the store that no listed package was matched to: where every package was
    /// matched, the drivers that are part of Windows, which neither route removes. Windows' own cleanup
    /// decides among the third-party packages, which is why those not offered are asserted only where
    /// the plan names exactly what goes.
    /// </summary>
    private IReadOnlyList<string> Unlisted(DriverStoreListing listing, List<PlanNote> notes)
    {
        var listed = new HashSet<string>(
            listing.Packages.Select(p => p.Folder).OfType<string>().Select(Path.TrimEndingDirectorySeparator),
            StringComparer.OrdinalIgnoreCase);

        var children = ChildDirectories.Under(_repository);

        if (children.Unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Windows would not list the folders in its driver store, so the clean cannot check afterwards "
                + "that the drivers that are part of Windows are still there."));
        }

        return
        [
            .. children.Directories
            .Select(child => LongPath.Display(child.FullName))
            .Where(child => !listed.Contains(child)),
        ];
    }

    /// <summary>The newest of <paramref name="dates"/>, or null where any folder could not be dated.</summary>
    private static DateTime? Newest(IReadOnlyList<DateTime?> dates)
    {
        return dates.Any(d => d is null) ? null : dates.Max();
    }

    /// <summary>
    /// The pass's listing, and with it Windows' cleanup asked what it would clear, so the handler's
    /// measurement runs beside pnputil's listing rather than after it.
    ///
    /// <para><b>Asked before the plan knows it needs the answer.</b> Elevated on Windows 11 24H2, the
    /// listing took 1.2 to 2.1 s and the survey 3.5 to 4.1 s, and the two together took as long as
    /// the survey alone. Where the listing shows nothing superseded, the survey is stopped, which the
    /// handler obeyed within 110 ms. Unelevated, <see cref="DiskCleanupHandlers.Survey"/> answers
    /// before it loads the handler, so the early start costs a registry read.</para>
    ///
    /// <para><b>Not asked while an update is unfinished</b>, which holds every package back in any
    /// case: Windows Update installs drivers, and nothing documents what the handler's measurement
    /// does beside it. The plan asks again before it reads the answer.</para>
    /// </summary>
    private Task<DriverStoreListing> ListingAsync(CancellationToken ct) => _listing.Get(() =>
    {
        var survey = UnfinishedUpdate.HoldsEverything(Servicing, Inspector) is null
            ? new EarlyDiskCleanupSurvey(_handlers, Handler, _volume, ct)
            : null;

        // A listing that failed is asked again, and the survey started with it is replaced.
        Interlocked.Exchange(ref _early, survey)?.Abandon();

        return _store.ListAsync(ct);
    });

    /// <summary>The survey started with the listing, handed to exactly one caller.</summary>
    private EarlyDiskCleanupSurvey? TakeEarlySurvey() => Interlocked.Exchange(ref _early, null);
}
