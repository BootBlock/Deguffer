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

    private readonly ISystemDirectories _system;
    private readonly IDriverStore _store;
    private readonly string _repository;

    /// <summary>
    /// The listing presence and planning both read, taken once per planning pass (G4): it is a
    /// subprocess and a SetupAPI call per package.
    /// </summary>
    private Task<DriverStoreListing>? _listing;

    public DriverStoreProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IWindowsServicing? servicing = null,
        IDiskCleanupHandlers? handlers = null,
        IDriverStore? store = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? HardLinkAwareScanner.Default,
            handlers: handlers,
            servicing: servicing)
    {
        _system = system ?? SystemDirectories.Current;
        _store = store ?? new DriverStore(Runner, _system);
        _repository = Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository");
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

    public override ProviderDescription Description { get; } = new()
    {
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
        _listing = null;
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

        return listing.Failure is not null || SupersededDrivers.Of(listing.Packages, _repository).Newest.Count > 0;
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(_repository, "This Windows keeps no driver store.") is { } nothing)
        {
            return nothing;
        }

        var listing = await ListingAsync(ct).ConfigureAwait(false);

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

        var notes = new List<PlanNote>(Considered(listing, drivers));
        var unlisted = Unlisted(listing, notes);

        // A third-party package that did not read, or whose folder Windows would not name, is also a
        // folder the listing does not account for, so an unlisted folder is Windows' own only where
        // every listed package was matched to its folder.
        var matched = listing.Unread == 0 && listing.Packages.All(p => p.Folder is not null);
        var kept = Protect(
        [
            .. KeptPaths(drivers.Newest, listing),
            .. matched ? unlisted.Select(folder => (folder, "A driver that is part of Windows, which pnputil does not list as third-party.")) : [],
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

        // Asked last, for the reason PreviousWindowsInstallationProvider asks last: the handler works
        // out what it would clear, and that is the one question here that can take a while.
        var volume = LongPath.Display(_system.SystemDrive);
        var survey = await Task.Run(() => Handlers.Survey(Handler, volume, ct), ct).ConfigureAwait(false);

        return survey.Answer switch
        {
            DiskCleanupAnswer.Unavailable => await ByPackageAsync(
                drivers, listing, survey, notes, [.. kept, .. matched ? [] : Protect([.. unlisted.Select(folder => (folder, NotAsked))])], ct).ConfigureAwait(false),
            _ when survey.MayOffer => await ByHandlerAsync(folders, volume, keep, matched, notes, kept, ct).ConfigureAwait(false),
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
            notes.Add(Unmatched);
        }

        var target = new DiskCleanupTarget(
            folders[0],
            "Older versions of drivers that have a newer version beside them, removed by Windows' own "
            + "driver package cleanup",
            Handler,
            volume,
            [.. folders.Skip(1)],
            Newest(folders, ct),
            RequiresElevation: true);

        var (steps, measured) = await PlanDiskCleanupsAsync([target], keep, ct).ConfigureAwait(false);

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            "Windows' own cleanup decides which older driver packages to remove, and keeps the newest "
            + "version of each driver and every driver a device is using. The figure is the "
            + $"{Count(folders.Count, "older package")} Deguffer found with a newer version beside them, so Windows may "
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
        var measured = await MeasureAllAsync(folders, ct).ConfigureAwait(false);
        var pnpUtil = DriverStore.PnpUtil(_system);

        IReadOnlyList<CleanupStep> steps =
        [
            .. drivers.Superseded.Select((superseded, i) => new RunCommandStep(
                pnpUtil,
                $"/delete-driver {superseded.Package.PublishedName}",
                $"Remove {Described(superseded.Package)}, which version {superseded.Newest.Version} of "
                + $"{superseded.Newest.Date:d MMMM yyyy} replaces, using Windows' own command")
            {
                Removes = superseded.Package.Folder,
                MeasuredPaths = [superseded.Package.Folder!],
                Estimated = measured.Sizes[i],
                LastWritten = DirectoryAge.Of(superseded.Package.Folder!, ct),
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
            .Select(folder => (folder, NotAsked));

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
                ? $"{Described(package)}, which a device is installed with."
                : $"{Described(package)}, the newest version of that driver here.");
        }
    }

    /// <summary>Why a folder stays where the plan names exactly what <c>pnputil</c> removes.</summary>
    private const string NotAsked = "A driver package pnputil was not asked to remove.";

    /// <summary>
    /// Said where Windows' cleanup does the work and a listed package could not be matched to its
    /// folder: that cleanup may rightly take the unmatched package, so no unlisted folder can be
    /// asserted afterwards. Where <c>pnputil</c> does the work, Deguffer names exactly what goes and
    /// every unlisted folder is asserted regardless.
    /// </summary>
    private static readonly PlanNote Unmatched = new(
        PlanNoteSeverity.Information,
        "Deguffer could not match every driver package Windows listed to its folder, so it cannot tell "
        + "which folders in the store hold drivers that are part of Windows, and the clean cannot check "
        + "afterwards that they are still there.");

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

    /// <summary>What the plan tells the user about the packages it did not consider or will not offer.</summary>
    private static IEnumerable<PlanNote> Considered(DriverStoreListing listing, SupersededDrivers drivers)
    {
        if (listing.Unread > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Deguffer could not read what Windows listed about {Count(listing.Unread, "driver package")}, "
                + "so none of them is offered.");
        }

        if (drivers.InUse.Count > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {Count(drivers.InUse.Count, "older package")} alone although a newer version is "
                + (drivers.InUse.Count == 1
                    ? "here: a device is installed with it."
                    : "here: a device is installed with each of them."));
        }

        if (drivers.Unplaced > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {Count(drivers.Unplaced, "older package")} alone: Windows did not say where in its "
                + "driver store they are.");
        }
    }

    private static string Described(DriverPackage package) =>
        $"{package.Provider}'s {package.OriginalName} version {package.Version} of {package.Date:d MMMM yyyy}";

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";

    /// <summary>The newest write among <paramref name="folders"/>, or null where any could not be dated.</summary>
    private static DateTime? Newest(IReadOnlyList<string> folders, CancellationToken ct)
    {
        var dates = folders.Select(folder => DirectoryAge.Of(folder, ct)).ToList();

        return dates.Any(d => d is null) ? null : dates.Max();
    }

    private Task<DriverStoreListing> ListingAsync(CancellationToken ct) => _listing ??= _store.ListAsync(ct);
}
