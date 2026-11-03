namespace Deguffer.Core.Execution;

/// <summary>
/// Where a <see cref="DirectoryRemover"/> removal spent its time: walking the tree, deleting its files
/// and links, and deleting its folders.
///
/// <para><b>The passes run beside each other.</b> A folder's files are deleted as soon as its listing
/// is complete, and a folder as soon as everything in it has been tried, so each pass is timed from
/// its first piece of work to its last. Added together they can come to more than the removal took.
/// See <see cref="TreeRemoval"/>.</para>
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
    /// <summary>
    /// The passes of two removals one step made, pass by pass, or whichever of them walked a tree. Null
    /// where neither did.
    /// </summary>
    public static RemovalPasses? Combine(RemovalPasses? first, RemovalPasses? second) => (first, second) switch
    {
        ({ } a, { } b) => new(a.Walk + b.Walk, a.Files + b.Files, a.Folders + b.Folders),
        _ => first ?? second,
    };
}
