using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The version gate an Nx cache was offered on, asked again at the clean, then the in-use check.
///
/// <para>An Nx 23.2 checkout of the same workspace can run between the preview and the clean, with its
/// daemon off, and leave a <c>-v3</c> index naming entries in the cache it shares, holding nothing open
/// when it exits. The in-use check sees no holder, so without this the cache would go and that index
/// would report its tasks as restored and restore nothing. See <see cref="NxCacheIndex"/>.</para>
/// </summary>
/// <param name="index">The folder holding the cache's index, as the plan located it.</param>
/// <param name="absentIsEmpty">As the plan asked it. See <see cref="NxCacheIndex.AllowsRemoval"/>.</param>
/// <param name="inUse">The in-use check the cache was cleared by.</param>
internal sealed class NxIndexCheck(string index, bool absentIsEmpty, IUseCheck inUse) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct) =>
        NxCacheIndex.Read(index).AllowsRemoval(absentIsEmpty)
            ? inUse.Ask(step, ct)
            : [new InUseNow(step.Path, "its index has changed since the preview to one an older Nx reads")];
}
