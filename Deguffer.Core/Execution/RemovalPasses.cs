namespace Deguffer.Core.Execution;

/// <summary>
/// Where a <see cref="DirectoryRemover"/> removal spent its time: gathering the tree, deleting its files
/// and links, and then its folders.
///
/// <para><b>So a slow clean can be told apart from a large one.</b> One tree of a few huge files is a
/// short walk and a short file pass. Five hundred small trees are a long walk and a long folder pass
/// for the same bytes. Without the passes, both read as one figure, and a report from a user's machine
/// could not say which it was.</para>
///
/// <para>A pass the removal never reached, because it was cancelled, is zero with no entries.</para>
/// </summary>
public sealed record RemovalPasses(PassTiming Walk, PassTiming Files, PassTiming Folders)
{
    public TimeSpan Elapsed => Walk.Elapsed + Files.Elapsed + Folders.Elapsed;
}
