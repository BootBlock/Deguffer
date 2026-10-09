using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What this machine protects from a removal picked out by hand, asked of one fresh set of providers:
/// Explore's refusal set (§7.1), and the places Storage's cleans delete in, which a duplicate copy
/// is never kept in (§7.4).
///
/// <para><b>The one builder for both pages</b>, so Explore and Duplicates refuse from the same
/// declarations rather than each constructing the providers its own way. Each build looks at the
/// machine afresh, as <see cref="ExploreActions.ForThisMachine"/> explains: the providers are
/// constructed again with their own <see cref="UserEnvironment"/> and
/// <see cref="LiveTreeInspector"/>, so what is on <c>PATH</c> and what is running are read now.</para>
///
/// <para><b>Storage's places are asked only when wanted.</b> Asking runs the tools that report
/// where their caches are, which Explore never needs, so the answer is started on the first
/// request and kept for the life of the instance.</para>
/// </summary>
public sealed class MachineProtections
{
    /// <summary>How many providers are asked at once, for the reason <see cref="ExploreActionPolicy.ForAsync"/> gives.</summary>
    private const int Asking = 8;

    private readonly IReadOnlyList<ICleanupProvider> _providers;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<StorageClean>>? _cleaned;

    private MachineProtections(ExploreActionPolicy policy, IReadOnlyList<ICleanupProvider> providers)
    {
        Policy = policy;
        _providers = providers;
    }

    /// <summary>Explore's policy, built from these providers.</summary>
    public ExploreActionPolicy Policy { get; }

    /// <summary>Asks this machine afresh, as each Explore scan and each duplicate search does.</summary>
    public static Task<MachineProtections> ForThisMachineAsync(CancellationToken ct = default)
    {
        var environment = new UserEnvironment();
        var providers = CleanupPlanner.CreateDefault(liveTrees: new LiveTreeInspector(), environment: environment).Providers;

        return ForAsync(SystemDirectories.Current, environment, VolumeInventory.Current, providers, ct);
    }

    /// <summary>The protections <paramref name="providers"/> declare, on this machine's directories.</summary>
    public static async Task<MachineProtections> ForAsync(
        ISystemDirectories system,
        IUserEnvironment environment,
        IVolumeInventory volumes,
        IEnumerable<ICleanupProvider> providers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        IReadOnlyList<ICleanupProvider> asked = [.. providers];

        return new MachineProtections(
            await ExploreActionPolicy.ForAsync(system, environment, volumes, asked, ct).ConfigureAwait(false),
            asked);
    }

    /// <summary>
    /// Every place a Storage clean can delete in, each with the clean that does. Asked of every
    /// provider whether or not its tool is installed, because a cache left by a tool since removed is
    /// still one a provider can clean.
    /// </summary>
    public Task<IReadOnlyList<StorageClean>> StorageCleansAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cleaned is null or { IsFaulted: true } or { IsCanceled: true })
            {
                _cleaned = Ask(ct);
            }

            return _cleaned;
        }
    }

    private async Task<IReadOnlyList<StorageClean>> Ask(CancellationToken ct)
    {
        var answers = new IReadOnlyList<CleanedPlace>[_providers.Count];

        await Parallel.ForAsync(
            0,
            _providers.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Asking, CancellationToken = ct },
            async (index, token) => answers[index] = await _providers[index].CleanedPlacesAsync(token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return [.. answers.SelectMany((places, index) => places.Select(place => new StorageClean(place, _providers[index].Name)))];
    }
}

/// <summary>A place a Storage clean can delete in, and the name of the clean the page shows.</summary>
public sealed record StorageClean(CleanedPlace Place, string Clean);
