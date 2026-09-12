namespace Deguffer.Core.Memory;

/// <summary>
/// One read of where the machine's memory is: the system figures, every process, and the services
/// the processes host.
///
/// <para>Stale as soon as it is read. Processes start and exit between the three reads that make it
/// up, and between one snapshot and the next, so nothing may act on one without asking Windows again
/// (§7.2).</para>
/// </summary>
public sealed record MemorySnapshot(SystemMemory System, ProcessMemoryTable Processes, ServiceTable Services);

/// <summary>
/// Reads a <see cref="MemorySnapshot"/>. The seam between what Windows reports and everything that
/// is decided from it, so those decisions are tested with a snapshot a test wrote rather than with
/// this machine's.
/// </summary>
public interface IMemorySource
{
    /// <summary>Read the machine now. Synchronous, because every call under it is.</summary>
    MemorySnapshot Read(CancellationToken ct);
}
