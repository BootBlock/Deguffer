namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How urgently the storage stack serves the reads of a volume's table, against every other read of
/// the same disk (<see cref="IMftVolume.Prioritise"/>).
/// </summary>
public enum ReadPriority
{
    /// <summary>As every other read is served. What a volume opened for its table reads at.</summary>
    Normal,

    /// <summary>
    /// Asks the storage stack to serve these reads after the reads at normal priority waiting on the
    /// same disk: for a build nobody waits on alone, while a walk is in flight on its disk
    /// (<see cref="BuildPriorities"/>).
    /// </summary>
    Low,
}
