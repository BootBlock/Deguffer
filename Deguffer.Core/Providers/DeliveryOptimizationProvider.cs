using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Delivery Optimization cache: the updates and Store apps Windows has downloaded, kept so this
/// machine can pass them on to other PCs rather than each downloading them again.
///
/// <para><b>Tier 1.</b> Everything in it is a downloaded payload that Windows fetches again if it ever
/// needs it, and it is kept for peers rather than for this machine. Clearing it costs a download that
/// may never happen, and nothing is lost.</para>
///
/// <para><b>§5.1: the command is the whole of it.</b> Deguffer never deletes a path here.
/// <c>Delete-DeliveryOptimizationCache</c> is Windows' own eviction, and it knows which files the
/// service is using and which it was told to keep. See <see cref="DeliveryOptimizationCache"/> for the
/// command, and for why the pinned files are never included.</para>
///
/// <para><b>The size decides, and it is Windows' figure.</b> The cache limits itself: by default to a
/// fifth of the free space and three days per file, and a machine set to download from Microsoft only
/// keeps nothing to share. So an empty cache is the common case, and Windows says so exactly, which
/// makes "Already clear" a true statement rather than a guess about a folder nobody listed.</para>
///
/// <para><b>§5.2.</b> <c>SoftwareDistribution</c>, where older Windows builds kept this cache, is
/// Windows Update's own folder, and its <c>DataStore</c> is the update history. The command reaches
/// neither, and §5.6 asserts afterwards that both are still standing and have not been emptied.</para>
///
/// <para><b>§5.6 and the service's own folder.</b> The folder Delivery Optimization keeps in the
/// Network Service profile is the tool's root, so the run asserts it is still standing. It is asserted
/// on its existence alone: what it holds is the command's to remove, and nobody outside the service
/// can list it to say which part is cache. An unelevated account is refused even that much, so there
/// the plan says the run cannot confirm the folder afterwards rather than protecting a path whose
/// every answer would be a refusal.</para>
/// </summary>
public sealed class DeliveryOptimizationProvider : CleanupProviderBase
{
    private readonly DeliveryOptimizationCache _cache;
    private readonly ISystemDirectories _system;

    /// <summary>Where Delivery Optimization keeps its own folder, in the Network Service profile.</summary>
    private readonly string _serviceFolder;

    public DeliveryOptimizationProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _cache = new DeliveryOptimizationCache(_system, Runner);
        _serviceFolder = Path.Combine(
            _system.WindowsDirectory, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft",
            "Windows", "DeliveryOptimization");
    }

    public override string Id => "delivery-optimization";

    public override string Name => "Delivery Optimization cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Windows downloads an update or app again from Microsoft if it needs one it had kept here, and "
        + "other PCs on your network download it themselves instead of from this one. Anything Windows "
        + "was told to keep stays.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Delivery Optimization, the download service behind Windows Update and the "
            + "Microsoft Store",
        Publisher = "Microsoft",
        Purpose = "Delivery Optimization keeps a copy of the updates and apps it downloads, so it can "
            + "pass them on to other PCs instead of each one downloading them again. Windows clears it "
            + "as files age and as space runs short, but it lets the cache grow to a fifth of the free "
            + "space on the drive.",
        Recommendation = "Deguffer asks Windows' own Delivery Optimization command to clear the cache, "
            + "and never deletes its files itself. Windows reports the size, so the figure is exact, "
            + "and files Windows was told to keep are left alone.",
    };

    /// <summary>
    /// The module's manifest, never a folder: the cache is where Windows keeps it, which the signed-in
    /// account may not be able to list. A manifest Windows would not describe may be there, so it is
    /// read as present and the plan says what Windows answered.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.FileMayExist(_cache.ModuleManifest));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (LongPath.ProbeFile(_cache.ModuleManifest) is PathPresence.Absent)
        {
            return EmptyPlan(
                "Windows' Delivery Optimization commands are not on this machine, so it keeps no "
                + "cache Deguffer can ask about.");
        }

        var reading = await _cache.ReadAsync(ct).ConfigureAwait(false);

        if (reading.Bytes is not { } bytes)
        {
            return UnexaminedPlan(
                $"Windows did not say how much its Delivery Optimization cache holds: {reading.Failure} "
                + "Nothing is offered rather than guessed at.");
        }

        if (bytes == 0)
        {
            return EmptyPlan("Windows reports that its Delivery Optimization cache is empty.");
        }

        var serviceFolder = ServiceFolder();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(
                    _cache.PowerShell,
                    DeliveryOptimizationCache.ClearArguments,
                    "Clear the Delivery Optimization cache using Windows' own command")
                {
                    Estimated = ScanSize.FromLengths(bytes),
                    MeasuredBy = _cache,
                },
            ],
            ProtectedPaths = [.. Protect(
                (Path.Combine(_system.WindowsDirectory, "SoftwareDistribution"),
                    "Windows Update's own folder, which the Delivery Optimization command does not clear."),
                (Path.Combine(_system.WindowsDirectory, "SoftwareDistribution", "DataStore"),
                    "The database Windows Update keeps its history of installed updates in.")),
                .. serviceFolder.Protected],
            Notes =
            [
                new PlanNote(
                    PlanNoteSeverity.Information,
                    "The figure is what Windows reports its Delivery Optimization cache holds. Any file "
                    + "Windows was told to keep is left in place, so the clean may free less."),
                .. serviceFolder.Notes,
            ],
        };
    }

    /// <summary>
    /// The service's own folder as §5.6 can assert it: on its existence where Windows describes it,
    /// and as a sentence where Windows refuses. See the class remarks for why it is never asked about
    /// its contents. Absent needs neither, because a folder that was never there cannot be lost.
    /// </summary>
    private (IReadOnlyList<ProtectedPath> Protected, IReadOnlyList<PlanNote> Notes) ServiceFolder() =>
        LongPath.ProbeDirectory(_serviceFolder) switch
        {
            PathPresence.Present =>
            (
                [new ProtectedPath(
                    _serviceFolder,
                    "Delivery Optimization's own folder, which its command clears inside and never removes.",
                    PathPresence.Present,
                    HeldContentBefore: false)],
                []
            ),
            PathPresence.Refused =>
            (
                [],
                [new PlanNote(
                    PlanNoteSeverity.Information,
                    "Windows does not let this account look at Delivery Optimization's own folder, so "
                    + "the clean cannot confirm afterwards that the folder is still there. Scanning as "
                    + "administrator lets it.")]
            ),
            _ => ([], []),
        };
}
