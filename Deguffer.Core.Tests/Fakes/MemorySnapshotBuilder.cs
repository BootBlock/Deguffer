using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Writes a <see cref="MemorySnapshot"/> for a test, in megabytes, with invented processes and services.
/// Everything not given takes a round default, so a test states only the figures it is about.
/// </summary>
internal sealed class MemorySnapshotBuilder
{
    public const long MiB = 1024 * 1024;

    private readonly List<ProcessMemory> _processes = [];
    private readonly List<RunningService> _services = [];
    private ProcessFigures _figures = ProcessFigures.Checked;
    private MemoryLists? _lists;
    private long _physical = 16_000 * MiB;
    private long _systemCache = 3_000 * MiB;
    private long _nonPagedPool = 200 * MiB;

    /// <param name="created">The creation time, as FILETIME ticks. Only the order of these matters.</param>
    public MemorySnapshotBuilder Process(int id, int parent, string name, long privateMiB, long created)
    {
        _processes.Add(new ProcessMemory(id, parent, name, CommitCharge: 2 * privateMiB * MiB, privateMiB * MiB, created));
        return this;
    }

    public MemorySnapshotBuilder Service(string name, int host)
    {
        _services.Add(new RunningService(name, $"{name} display name", host));
        return this;
    }

    /// <summary>Turn the undocumented figures off the way <see cref="ProcessMemoryTable"/> does for this verdict.</summary>
    public MemorySnapshotBuilder Figures(ProcessFigures figures)
    {
        _figures = figures;
        return this;
    }

    public MemorySnapshotBuilder Lists(long zeroedMiB, long freeMiB, long modifiedMiB, long standbyMiB)
    {
        _lists = new MemoryLists(zeroedMiB * MiB, freeMiB * MiB, modifiedMiB * MiB, standbyMiB * MiB);
        return this;
    }

    public MemorySnapshotBuilder System(long physicalMiB, long systemCacheMiB, long nonPagedPoolMiB)
    {
        (_physical, _systemCache, _nonPagedPool) = (physicalMiB * MiB, systemCacheMiB * MiB, nonPagedPoolMiB * MiB);
        return this;
    }

    public MemorySnapshot Build()
    {
        var on = _figures == ProcessFigures.Checked;

        var processes = _processes
            .Select(process => on ? process : process with { PrivateWorkingSet = null, CreationTime = null })
            .ToArray();

        var system = new SystemMemory(
            PhysicalTotal: _physical,
            Available: _physical / 3,
            CommitCharge: _physical / 2,
            CommitLimit: _physical * 3 / 2,
            SystemCache: _systemCache,
            PagedPool: 400 * MiB,
            NonPagedPool: _nonPagedPool,
            Lists: _lists,
            ListState: _lists is null ? MemoryListState.NotReturned : MemoryListState.Checked);

        return new MemorySnapshot(
            system,
            new ProcessMemoryTable(processes, _figures, Complete: true),
            new ServiceTable([.. _services], ServiceListing.Listed));
    }
}
