namespace Deguffer.Core.Memory;

/// <summary>
/// Reads this machine's <see cref="MemorySnapshot"/>: the system figures, the process table and the
/// service hosts, with the undocumented parts of each checked before they are handed on.
///
/// <para>This composes and does nothing else. Each reader only calls Windows, and every decision
/// about what Windows returned is made in a type a test can reach: the parsers,
/// <see cref="ProcessFigureCheck"/>, <see cref="MemoryListCheck"/>, <see cref="OwnPrivateWorkingSet"/>
/// and <see cref="ServiceListingProgress"/>.</para>
///
/// <para>One instance for the process (G5). Its readers keep their buffers between reads, and its
/// check keeps what cannot change, and the lock is what makes both safe if two callers ever ask at
/// once.</para>
/// </summary>
public sealed class MemorySource : IMemorySource
{
    public static readonly MemorySource Default = new();

    private readonly Lock _gate = new();
    private readonly SystemMemoryReader _system = new();
    private readonly ProcessTableReader _processes = new();
    private readonly ServiceTableReader _services = new();
    private readonly ProcessFigureCheck _figures = new(
        Environment.ProcessId,
        narrowOnWideWindows: Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess);

    private MemorySource()
    {
    }

    public MemorySnapshot Read(CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var system = _system.Read();

            ct.ThrowIfCancellationRequested();
            var parsed = _processes.Read();
            var figures = _figures.Judge(parsed, OwnProcessCounters.Read);

            ct.ThrowIfCancellationRequested();
            var services = _services.Read();

            return new MemorySnapshot(system, ProcessMemoryTable.From(parsed, figures), services);
        }
    }
}
