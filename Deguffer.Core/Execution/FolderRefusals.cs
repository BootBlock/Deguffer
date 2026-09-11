using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Folders Windows would not let a removal take for a reason of their own, counted by
/// <see cref="RefusalReason"/>.
///
/// <para><b>A reason of their own, never what they hold.</b> A folder still holding a refused file,
/// a file the guard kept or an entry something is using cannot be removed either, and each of those
/// causes is already counted where it happened. Counting every folder above it as well would tell
/// the reader one open file kept a whole chain of folders, when the folders were only where the file
/// was. What is left is the folder Windows refused itself: most often one a program is working in,
/// which Windows will not remove until that program moves out of it or closes.</para>
///
/// <para><b>Apart from <see cref="Refusals"/>, because a folder holds no bytes of its own.</b> Those
/// figures are what the next preview leaves out of its estimate, and a refused folder changes no
/// figure there. Folded together, folders would reach a sentence about what the next preview leaves
/// out, and it leaves out none of them.</para>
/// </summary>
public readonly record struct FolderRefusals(int InUse, int Denied)
{
    public static readonly FolderRefusals None = default;

    public bool IsEmpty => InUse == 0 && Denied == 0;

    /// <summary>One folder, refused for <paramref name="reason"/>.</summary>
    public static FolderRefusals One(RefusalReason reason) => reason == RefusalReason.InUse
        ? new FolderRefusals(InUse: 1, Denied: 0)
        : new FolderRefusals(InUse: 0, Denied: 1);

    public static FolderRefusals operator +(FolderRefusals left, FolderRefusals right) =>
        new(left.InUse + right.InUse, left.Denied + right.Denied);
}
