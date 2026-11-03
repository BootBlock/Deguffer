namespace Deguffer.Core.Execution;

/// <summary>How long one pass of a removal took, and how many entries it dealt with in that time.</summary>
/// <param name="Entries">
/// What the pass handled, whatever became of each: every entry the walk gathered, every file and link
/// the file pass tried, and every folder the folder pass tried. A refused entry was handled as much as
/// a removed one, because what slows a pass is the asking, not the answer.
/// </param>
public readonly record struct PassTiming(TimeSpan Elapsed, long Entries)
{
    /// <summary>The same pass over two removals one step made, such as an index and the folder it indexes.</summary>
    public static PassTiming operator +(PassTiming a, PassTiming b) => new(a.Elapsed + b.Elapsed, a.Entries + b.Entries);
}
