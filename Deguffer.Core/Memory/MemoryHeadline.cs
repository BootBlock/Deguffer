using Deguffer.Core.Scanning;

namespace Deguffer.Core.Memory;

/// <summary>
/// The two figures a memory view leads with (§7.2): commit charge against the commit limit, and
/// available memory beside it.
///
/// <para>Commit charge leads because it is the figure that states the failure a user feels: when it
/// reaches the limit, allocations fail. A headline of memory "in use" would present a cache as a
/// problem, since Windows fills memory nothing else needs with one.</para>
/// </summary>
public static class MemoryHeadline
{
    public static string Commit(SystemMemory system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return $"{FreeSpace.Format(system.CommitCharge)} of {FreeSpace.Format(system.CommitLimit)} committed";
    }

    public static string Available(SystemMemory system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return $"{FreeSpace.Format(system.Available)} available";
    }

    /// <summary>
    /// How much of the commit limit is committed, from 0 to 1, for a bar beside the words. Zero where
    /// Windows reported no limit, which is not a machine with nothing committed but one that did not
    /// answer.
    /// </summary>
    public static double CommittedFraction(SystemMemory system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.CommitLimit > 0
            ? Math.Clamp((double)system.CommitCharge / system.CommitLimit, 0, 1)
            : 0;
    }
}
