namespace Deguffer.Core.Memory;

/// <summary>
/// One snapshot from a <see cref="MemoryFeed"/>, and when it was read. Together, because the time is
/// about this snapshot: a page that shows one reading and throws the next away must still say when
/// the one it shows was read.
/// </summary>
public readonly record struct MemoryReading(MemorySnapshot Snapshot, DateTimeOffset Read);
