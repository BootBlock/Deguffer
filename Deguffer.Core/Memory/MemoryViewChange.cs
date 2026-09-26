namespace Deguffer.Core.Memory;

/// <summary>
/// Why a memory view is being shown a different tree or a different node of one, which decides what
/// becomes of whatever the reader picked out in it.
/// </summary>
public enum MemoryViewChange
{
    /// <summary>A reading arrived: the same subject, measured again.</summary>
    Reading,

    /// <summary>The reader moved to another part of the tree.</summary>
    Navigation,
}
