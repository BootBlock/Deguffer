using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every restore point but the newest, removed by System Restore itself. Restore points are kept as
/// shadow copies in <c>System Volume Information</c>, which no folder in a size picture shows, and System
/// Protection lets them grow to a share of the drive that users report set as high as 20%.
///
/// <para><b>§5.1: System Restore's own removal, <c>SRRemoveRestorePoint</c>.</b> Disk Cleanup's
/// <em>clean up system restore and shadow copies</em> is the shape followed, keeping the newest, but not
/// the route: it is no handler Windows registers, so nothing outside Disk Cleanup can run it, and its own
/// text says it removes other shadow copies too. <c>vssadmin delete shadows</c> is not the route either:
/// it names shadow copies, and Windows documents no link from a restore point to its shadow copies, so
/// choosing which to delete would be a guess about another program's data.</para>
///
/// <para><b>Tier 3.</b> A restore point removed is a way back that is gone for good, which is §3's
/// "gone permanently". The newest is never offered, so the machine always keeps one.</para>
///
/// <para><b>§5.6 without a path.</b> The run proves afterwards that the newest restore point, every shadow
/// copy that cannot be System Restore's, and System Protection's limit on each volume are as they were.
/// A plan that could not list the shadow copies cannot make that promise, so it offers nothing. Where
/// Windows has point-in-time restore, which sets the same limit itself, a changed limit is unverified
/// rather than failed.</para>
///
/// <para><b>Point-in-time restore stops the row.</b> Windows 11 24H2 and later can keep a daily shadow copy
/// of the whole system volume in the same storage, under the same limit. Windows does not say which shadow
/// copies are that feature's, or whether System Restore lists them, and they pass every test that would
/// rule a copy out. Where it records a snapshot, or its record cannot be read, the clean could neither
/// prove afterwards that they survived nor keep the newest restore point by the right list, so nothing is
/// offered.</para>
///
/// <para><b>System Protection's limit is named, never changed.</b> Lowering it is how Windows keeps less
/// space for restore points in future, and it is a setting rather than a removal, which Deguffer does
/// not make for the user (<c>docs/cache-locations.md</c>, "Why not Disk Cleanup or Storage Sense").</para>
/// </summary>
public sealed class RestorePointProvider : CleanupProviderBase
{
    /// <summary>
    /// Read by planning and given to the executor that removes, so the plan and the clean ask the same
    /// instance. See <see cref="CleanupProviderBase"/>'s <c>protection</c>.
    /// </summary>
    private readonly ISystemProtection _protection;

    /// <summary>The listing presence and planning both read, taken once per planning pass (G4).</summary>
    private readonly PassMemo<Task<RestorePointListing>> _listing = new();

    /// <summary>
    /// The rest of the plan's questions, each asked once per planning pass, so the pass's start and the
    /// plan share one answer. The clean asks the machine afresh for what it proves.
    /// </summary>
    private readonly PassMemo<Task<PointInTimeRestore>> _pointInTime = new();

    private readonly PassMemo<Task<ShadowCopyListing>> _copies = new();
    private readonly PassMemo<Task<IReadOnlyList<VolumeShadowStorage>>> _storage = new();

    public RestorePointProvider(
        ISystemProtection protection,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            protection: protection)
    {
        _protection = protection;
    }

    public override string Id => "restore-points";

    public override string Name => "Older restore points";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Windows can no longer be returned to any of these restore points. The newest restore point stays, so "
        + "System Restore can still undo whatever changed since it was made, and Windows goes on making new ones "
        + "as before. Earlier versions of files kept with a removed restore point go with it.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "System Restore",
        Publisher = "Microsoft",
        Purpose = "Windows saves a restore point before updates, driver installations and some program "
            + "installations, so System Restore can return Windows to how it was. Each is kept as a shadow copy "
            + "of the drive, hidden in System Volume Information, and System Protection lets them grow to a share "
            + "of the drive before Windows removes the oldest.",
        Recommendation = "Removed by System Restore itself, one restore point at a time, always keeping the "
            + "newest. To keep less space for restore points in future, lower the limit in System Protection "
            + "(Control Panel, System, System Protection, Configure). Where Windows has point-in-time restore, "
            + "its maximum usage (Settings, System, Recovery) is the same limit. Deguffer never changes either.",
    };

    public override void InvalidateCaches()
    {
        _listing.Forget();
        _pointInTime.Forget();
        _copies.Forget();
        _storage.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// The plan's chain of questions asked as the pass begins, each only where the answer before it lets
    /// the plan go on, as the plan asks them: System Restore's list, point-in-time restore's record, the
    /// Volume Shadow Copy service's list and the storage each volume keeps. Each is a call into a system
    /// service in another process, and none of them waits on anything the plan reads from the disk.
    ///
    /// <para><b>It takes no place under the pass's bound on tool queries.</b> None of them starts a
    /// process: their wait is on the services, not on the processor. An unelevated account is refused the
    /// first at once, which ends the chain there.</para>
    /// </summary>
    public override Task BeginPass(PassStart start) => start.AskAtOnce(async ct =>
    {
        if (!MayOffer(await ListingAsync(ct).ConfigureAwait(false))
            || (await PointInTimeAsync(ct).ConfigureAwait(false)).MayKeepSnapshots
            || !SeesRestorePoints(await CopiesAsync(ct).ConfigureAwait(false)))
        {
            return;
        }

        await StorageAsync(ct).ConfigureAwait(false);
    });

    /// <summary>
    /// A restore point older than the newest. A listing Windows refused or failed is no evidence that
    /// there is none, so the row appears and its plan says what Windows answered.
    /// </summary>
    public override async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var listing = await ListingAsync(ct).ConfigureAwait(false);

        return listing.Answer is not ListingAnswer.Listed || listing.Points.Count > 1;
    }

    /// <summary>
    /// None: System Restore removes each point as a shadow copy of a volume, so no file a duplicate
    /// search can see is deleted.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var listing = await ListingAsync(ct).ConfigureAwait(false);

        if (Unlisted(listing.Answer, listing.Failure, "its restore points") is { } unlisted)
        {
            return unlisted;
        }

        if (!MayOffer(listing) || listing.Newest is not { } newest)
        {
            return EmptyPlan(listing.Points.Count == 0
                ? "System Restore keeps no restore point on this machine."
                : "System Restore keeps only one restore point here, and the newest is never offered.");
        }

        var pointInTime = await PointInTimeAsync(ct).ConfigureAwait(false);

        if (pointInTime.MayKeepSnapshots)
        {
            return UnexaminedPlan(SharedWithPointInTimeRestore(pointInTime));
        }

        var copies = await CopiesAsync(ct).ConfigureAwait(false);

        if (!SeesRestorePoints(copies))
        {
            var uncheckable = Unlisted(copies.Answer, copies.Failure, "the shadow copies on this machine")
                ?? UnexaminedPlan(
                    $"The Volume Shadow Copy service listed no shadow copy that could hold a restore point, although "
                    + $"System Restore lists {listing.Points.Count}, so its list cannot be complete.");

            return uncheckable with
            {
                Notes =
                [
                    .. uncheckable.Notes,
                    new PlanNote(
                        PlanNoteSeverity.Information,
                        "Without that list, the clean could not check afterwards that shadow copies other programs "
                        + "made are still there, so nothing is offered."),
                ],
            };
        }

        var storage = await StorageAsync(ct).ConfigureAwait(false);
        var stated = storage.Where(volume => volume.Storage.Statement is Statement.Stated).ToList();

        if (stated.Count == 0)
        {
            return UnexaminedPlan(
                "Windows did not state how much space its restore points and shadow copies take, so nothing is "
                + "offered rather than guessed at.");
        }

        var removes = listing.Points
            .Where(point => point.SequenceNumber != newest.SequenceNumber)
            .OrderBy(point => point.SequenceNumber)
            .ToList();
        var others = copies.Copies.Where(copy => !copy.CouldBeRestorePoint).ToList();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RemoveRestorePointsStep(
                    removes,
                    newest,
                    $"Remove {Count(removes.Count, "restore point")} older than the newest, using System Restore's own removal")
                {
                    // All the storage Windows states, which holds the restore point kept and every other
                    // program's shadow copies too. Windows gives no figure for one restore point, so this is
                    // the most the removal could free, and the run reports Windows' own before and after.
                    Estimated = ScanSize.Ceiling(stated.Sum(volume => volume.Storage.UsedBytes)),
                    LastWritten = removes[^1].Created,
                    RequiresElevation = true,
                    OtherCopies = others,
                    Storage = storage,
                },
            ],
            Notes = [.. Notes(removes, newest, others.Count, stated, pointInTime.Installed)],
        };
    }

    /// <summary>
    /// A plan for a listing Windows did not give, or null where it did. A refusal is the one answer
    /// scanning as administrator changes, so it is the one that says so.
    /// </summary>
    private CleanupPlan? Unlisted(ListingAnswer answer, string? failure, string what) => answer switch
    {
        ListingAnswer.NeedsElevation => UnexaminedPlan(
            $"Windows lists {what} only for an administrator, and only an administrator can remove a restore "
            + "point. Scanning as administrator lets Deguffer ask."),
        ListingAnswer.Failed => UnexaminedPlan($"{failure} Nothing is offered rather than guessed at."),
        _ => null,
    };

    /// <summary>Why a machine where point-in-time restore may keep snapshots is offered nothing.</summary>
    private static string SharedWithPointInTimeRestore(PointInTimeRestore pointInTime)
    {
        const string why =
            "Point-in-time restore keeps its snapshots as shadow copies in the same storage as restore points, and "
            + "Windows does not say which shadow copies are its own, or whether System Restore lists them as restore "
            + "points.";

        return pointInTime.Snapshots is PointInTimeSnapshots.Unread
            ? $"{pointInTime.Failure} {why} Without that record, the clean could not prove afterwards that they "
                + "survived, so nothing is offered."
            : $"Point-in-time restore records snapshots on this machine. {why} The clean could not prove afterwards "
                + "that they survived, so nothing is offered.";
    }

    private static IEnumerable<PlanNote> Notes(
        IReadOnlyList<RestorePoint> removes,
        RestorePoint newest,
        int others,
        IReadOnlyList<VolumeShadowStorage> stated,
        bool pointInTimeRestore)
    {
        yield return new PlanNote(
            PlanNoteSeverity.Information,
            $"Kept: the restore point {newest.Label}, the newest. Removed: "
            + string.Join("; ", removes.Select(point => $"the restore point {point.Label}")) + ".");

        yield return new PlanNote(
            PlanNoteSeverity.Information,
            "The figure is all the space Windows' restore points and shadow copies take, as Windows states it. It "
            + "includes the restore point that stays and any shadow copy another program made, and Windows gives no "
            + "figure for one restore point, so the clean frees less. It reports what Windows measures afterwards.");

        if (others > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"{Count(others, "shadow copy", "shadow copies")} on this machine cannot be System Restore's, and the "
                + "clean checks afterwards that each is still there.");
        }

        yield return new PlanNote(
            PlanNoteSeverity.Information,
            "Windows does not say which shadow copies belong to which restore point. System Restore removes its "
            + "own, and on some editions of Windows a shadow copy that also holds earlier versions of files, or a "
            + "Windows backup, goes with the restore point it belongs to.");

        // A volume Windows keeps no storage on answers with a limit of zero, which is no setting to name.
        var limits = stated.Where(volume => volume.Storage.MaximumBytes != 0).ToList();

        if (limits.Count > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                "System Protection's limit is "
                + string.Join(", ", limits.Select(volume => $"{Limit(volume.Storage.MaximumBytes)} on {volume.Volume}"))
                + ". Lowering it in System Protection keeps less space for restore points in future."
                + (pointInTimeRestore
                    ? " Point-in-time restore shares this limit, and where it is on, its maximum usage in Settings, "
                        + "System, Recovery sets it."
                    : string.Empty)
                + " Deguffer does not change it.");
        }
    }

    private static string Limit(long? maximum) => maximum is { } bytes ? FreeSpace.Format(bytes) : "no limit";

    private static string Count(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    /// <summary>Whether <paramref name="listing"/> holds a restore point older than the newest, which the rest of the plan is asked for.</summary>
    private static bool MayOffer(RestorePointListing listing) =>
        listing.Answer is ListingAnswer.Listed && listing.Newest is not null && listing.Points.Count >= 2;

    /// <summary>
    /// Whether <paramref name="copies"/> is a list the clean can prove other programs' shadow copies
    /// against. Every restore point is kept as a shadow copy, so a list holding none that could be one
    /// is not seeing them, and what it says is absent could not be proved standing afterwards.
    /// </summary>
    private static bool SeesRestorePoints(ShadowCopyListing copies) =>
        copies.Answer is ListingAnswer.Listed && copies.Copies.Any(copy => copy.CouldBeRestorePoint);

    private Task<RestorePointListing> ListingAsync(CancellationToken ct) =>
        _listing.Get(() => Task.Run(_protection.ListRestorePoints, ct));

    private Task<PointInTimeRestore> PointInTimeAsync(CancellationToken ct) =>
        _pointInTime.Get(() => Task.Run(_protection.ReadPointInTimeRestore, ct));

    private Task<ShadowCopyListing> CopiesAsync(CancellationToken ct) =>
        _copies.Get(() => Task.Run(_protection.ListShadowCopies, ct));

    private Task<IReadOnlyList<VolumeShadowStorage>> StorageAsync(CancellationToken ct) =>
        _storage.Get(() => Task.Run(_protection.ReadStorage, ct));
}
