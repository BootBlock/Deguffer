namespace Deguffer.Core.Memory;

/// <summary>
/// Reads this machine's <see cref="MemorySnapshot"/>: the system figures, the process table and the
/// service hosts, with the undocumented parts of each checked before they are handed on.
///
/// <para>One instance for the process (G5). Its readers keep their buffers between reads, and the
/// lock is what makes that safe if two callers ever ask at once.</para>
/// </summary>
public sealed class MemorySource : IMemorySource
{
    public static readonly MemorySource Default = new();

    private readonly Lock _gate = new();
    private readonly SystemMemoryReader _system = new();
    private readonly ProcessTableReader _processes = new();
    private readonly ServiceTableReader _services = new();

    /// <summary>
    /// Whether the process table's layout has been found sound once. Kept for the life of the
    /// process, because a layout belongs to the Windows build, and the build cannot change under a
    /// running process. A failure is not kept: it may be two readings of a moving figure landing far
    /// apart, so the next read checks again.
    /// </summary>
    private bool _layoutChecked;

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
            var figures = _layoutChecked
                ? ProcessFigures.Checked
                : ProcessFigureCheck.Judge(parsed, Environment.ProcessId, OwnProcessCounters.Read(), NarrowOnWideWindows);

            _layoutChecked = figures == ProcessFigures.Checked;

            ct.ThrowIfCancellationRequested();
            var services = _services.Read();

            return new MemorySnapshot(system, ProcessMemoryTable.From(parsed, figures), services);
        }
    }

    private static bool NarrowOnWideWindows => Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess;
}
