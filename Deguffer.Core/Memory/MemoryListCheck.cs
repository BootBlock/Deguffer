namespace Deguffer.Core.Memory;

/// <summary>
/// Decides whether the undocumented memory lists can be believed, by the one sum Windows documents:
/// available memory is the standby, free and zeroed pages together.
/// </summary>
internal static class MemoryListCheck
{
    /// <summary>
    /// The drift allowed between the two reads: a fiftieth of physical memory, and never less than
    /// 64 MiB. They are separate calls, and a machine can move that much in between. Measured, the
    /// two agreed to within a thousandth of a percent; a list read at the wrong offset is off by whole
    /// lists, not by a fiftieth.
    /// </summary>
    private const long MarginFloor = 64L * 1024 * 1024;

    public static bool Agrees(MemoryLists lists, long available, long physicalTotal) =>
        Math.Abs(lists.Zeroed + lists.Free + lists.Standby - available)
            <= Math.Max(MarginFloor, physicalTotal / 50);
}
