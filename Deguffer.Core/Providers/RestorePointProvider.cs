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
/// A plan that could not list the shadow copies cannot make that promise, so it offers nothing.</para>
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
    private Task<RestorePointListing>? _listing;

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
            scanner ?? DirectoryScanner.Default,
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

    public override ProviderDescription Description { get; } = new()
    {
        Application = "System Restore",
        Publisher = "Microsoft",
        Purpose = "Windows saves a restore point before updates, driver installations and some program "
            + "installations, so System Restore can return Windows to how it was. Each is kept as a shadow copy "
            + "of the drive, hidden in System Volume Information, and System Protection lets them grow to a share "
            + "of the drive before Windows removes the oldest.",
        Recommendation = "Removed by System Restore itself, one restore point at a time, always keeping the "
            + "newest. To keep less space for restore points in future, lower the limit in System Protection "
            + "(Control Panel, System, System Protection, Configure). Deguffer never changes that setting.",
    };

    public override void InvalidateCaches()
    {
        _listing = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// A restore point older than the newest. A listing Windows refused or failed is no evidence that
    /// there is none, so the row appears and its plan says what Windows answered.
    /// </summary>
    public override async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var listing = await ListingAsync(ct).ConfigureAwait(false);

        return listing.Answer is not ListingAnswer.Listed || listing.Points.Count > 1;
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var listing = await ListingAsync(ct).ConfigureAwait(false);

        if (Unlisted(listing.Answer, listing.Failure, "its restore points") is { } unlisted)
        {
            return unlisted;
        }

        if (listing.Newest is not { } newest || listing.Points.Count < 2)
        {
            return EmptyPlan(listing.Points.Count == 0
                ? "System Restore keeps no restore point on this machine."
                : "System Restore keeps only one restore point here, and the newest is never offered.");
        }

        var copies = await Task.Run(_protection.ListShadowCopies, ct).ConfigureAwait(false);

        // Every restore point is kept as a shadow copy, so a list holding none that could be one is not
        // seeing them, and what it says is absent could not be proved standing afterwards.
        var blind = copies.Answer is ListingAnswer.Listed && !copies.Copies.Any(copy => copy.CouldBeRestorePoint)
            ? UnexaminedPlan(
                $"The Volume Shadow Copy service listed no shadow copy that could hold a restore point, although "
                + $"System Restore lists {listing.Points.Count}, so its list cannot be complete.")
            : null;

        if ((blind ?? Unlisted(copies.Answer, copies.Failure, "the shadow copies on this machine")) is { } uncheckable)
        {
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

        var storage = await Task.Run(_protection.ReadStorage, ct).ConfigureAwait(false);
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
            Notes = [.. Notes(removes, newest, others.Count, stated)],
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

    private static IEnumerable<PlanNote> Notes(
        IReadOnlyList<RestorePoint> removes,
        RestorePoint newest,
        int others,
        IReadOnlyList<VolumeShadowStorage> stated)
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
                + ". Lowering it in System Protection keeps less space for restore points in future. Deguffer does "
                + "not change it.");
        }
    }

    private static string Limit(long? maximum) => maximum is { } bytes ? FreeSpace.Format(bytes) : "no limit";

    private static string Count(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    private Task<RestorePointListing> ListingAsync(CancellationToken ct) =>
        _listing ??= Task.Run(_protection.ListRestorePoints, ct);
}
