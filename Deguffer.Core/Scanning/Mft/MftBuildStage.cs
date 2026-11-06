namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One stage of reading a volume's file table and building from it, in the order a build runs them.
/// <see cref="MftStageTimings"/> times each one apart, so a slow build can be traced to the drive or
/// to one of the stages that run on one thread after the drive has finished.
/// </summary>
public enum MftBuildStage
{
    /// <summary>Reading <c>$MFT</c>'s <c>$BITMAP</c>, which says which records are in use.</summary>
    BitmapRead,

    /// <summary>The first pass: every record in use read and parsed, on several threads at once.</summary>
    FirstPass,

    /// <summary>Reading the attribute lists kept outside the table and folding in what they name.</summary>
    ExtensionLists,

    /// <summary>Handing on the records held back for their extension records, one at a time.</summary>
    DeferredRecords,

    /// <summary>Making each of a file's other names a node, in an order that does not depend on the threads.</summary>
    LinkPlacement,

    /// <summary>The index's inversion of the parent links into each directory's children.</summary>
    ChildLinks,

    /// <summary>Explore's assembly of the tree under the folder it is rooted at.</summary>
    TreeAssembly,
}
