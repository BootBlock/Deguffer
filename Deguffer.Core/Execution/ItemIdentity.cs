namespace Deguffer.Core.Execution;

/// <summary>
/// What one item a provider offers <em>is</em>, independent of where it sits on disk, so that a
/// decision about it can outlive the path it was found at.
///
/// <para><b>Deliberately not <see cref="CleanupStep.SelectionKey"/>.</b> That key is the path for
/// every deletion, and it is documented as unique within one plan and compared across nothing. It is
/// right for a tick, which lasts one preview. It is wrong for a decision that has to survive a
/// relocated cache, a renamed profile or a project moved to another folder, because every one of
/// those changes the path and leaves the item the same. A keep entry matched on the path would then
/// silently stop matching, and the item would be offered again: the one direction a protection must
/// not fail in. It would also write absolute paths into a preferences file.</para>
///
/// <para>Supplied by the provider, because only the provider knows what makes one of its items the
/// same item next time. A step with no identity cannot be kept, which is the honest answer for an
/// item whose only name is where it is.</para>
/// </summary>
/// <param name="Key">
/// What matches this item from one scan to the next. Unique within the provider that supplies it,
/// and compared without regard to case, because most keys are directory names and NTFS does not
/// distinguish theirs.
/// </param>
/// <param name="Name">
/// What the user recognises the item by. Stored beside the key so that a list of kept items can
/// still say what each one is after the scan that found it has gone.
/// </param>
public sealed record ItemIdentity(string Key, string Name);
