using Deguffer.Core.Viewing;

namespace Deguffer.Core.Memory;

/// <summary>When the Memory page reads the machine through its <see cref="MemoryFeed"/>.</summary>
public static class MemoryWatch
{
    /// <summary>
    /// Whether the page reads the machine while it is in <paramref name="sight"/>: only where the reader
    /// can see it. A page behind other windows, or in a minimised or hidden one, would otherwise go on
    /// reading, building a tree and shading the picture every couple of seconds for nobody.
    /// </summary>
    public static bool Wanted(Sight sight) => sight == Sight.Seen;
}
