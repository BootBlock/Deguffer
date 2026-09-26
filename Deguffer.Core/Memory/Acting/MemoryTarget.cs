namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// Which node of a memory picture is a program a user could ask to close, and which is not (§7.2.1).
///
/// <para><b>What Memory shows and what Memory would act on are different sets</b>, and this is where
/// the two part. In Core rather than in the page, because a rule that exists only inside a view-model
/// is a rule nothing can hold Deguffer to — and the picture draws one part of Windows, the
/// compression store, against the process that happens to hold it. A page that read "this node has a
/// process behind it" as "this node is a program" would offer to close that.</para>
///
/// <para>It is the first of §7.2.1's two questions, and the smaller one: this says whether there is a
/// program here at all, and <see cref="MemoryActionPolicy"/> says whether Deguffer will ask it
/// anything.</para>
/// </summary>
public static class MemoryTarget
{
    /// <summary>
    /// What to say about a node that is not a program. Here rather than in the page, for the reason
    /// every other refusal sentence is in Core (§7.2.1): a sentence that exists only inside a view is
    /// a sentence nothing can hold Deguffer to.
    ///
    /// <para>A <see cref="MemoryVerdict"/> rather than a bare string, so the one refusal this type
    /// decides reaches a caller in the same shape as the ten
    /// <see cref="MemoryActionPolicy"/> decides.</para>
    /// </summary>
    public static readonly MemoryVerdict NotAProgram = MemoryVerdict.Refuse(
        "This is a part of the picture rather than a program. Deguffer closes programs, and nothing "
        + "here is one to ask.");

    /// <summary>
    /// The program <paramref name="node"/> stands for, or null where it stands for something else.
    ///
    /// <para>A process and its own share are the same program: the share is that process's own pages,
    /// drawn inside it as a folder's loose files are drawn beside its subfolders. Every other node is
    /// a part of the picture rather than something the user picked out of the machine.</para>
    /// </summary>
    /// <param name="node">
    /// A node of <paramref name="tree"/>. A number outside it answers null rather than throwing: every
    /// reading renumbers the nodes, and a caller holding one from the reading before is asking about
    /// something that has gone.
    /// </param>
    public static ProcessMemory? Of(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (!tree.Holds(node))
        {
            return null;
        }

        return tree.PartOf(node) is MemoryPart.Process or MemoryPart.OwnShare
            ? tree.ProcessOf(node)
            : null;
    }
}
