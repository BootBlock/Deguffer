using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// Whether Windows would let this process delete a file, asked the only way that gives the answer a
/// deletion would get.
///
/// <para><b>The security descriptor cannot answer it.</b> The case that made this necessary was a
/// file whose DACL granted its owner full control, with no deny entry and no integrity label, which
/// still refused <c>DELETE</c> to its owner and to an administrator: a filter driver below the ACL
/// was refusing the open. <c>AccessCheck</c> against the descriptor would have said yes. So the
/// file is opened for deletion exactly as <c>DeleteFileW</c> opens it (<see cref="EntryDeletion.Open"/>),
/// and closed again at once without the disposition that would delete it.</para>
///
/// <para><b>It is not free of side effects, and the callers are chosen for that.</b> While the
/// handle is open, another program's open that does not share delete access fails. The handle lives
/// for microseconds and a refused open creates none, which is why this is asked only inside the
/// places a previous clean found Windows refusing, where most files usually still refuse — see
/// <see cref="Execution.RefusalCheck"/>.</para>
///
/// <para><b>One refusal it cannot see.</b> An executable something is running from opens for
/// deletion and then refuses the deletion itself — observed with a running copy of a system
/// executable, which this open accepted and <c>File.Delete</c> then refused with access denied. So
/// this answers that it would go, the removal reports it refused, and the next preview offers it
/// again until the program exits. A library a running program has loaded is held the same way. The
/// only exact question is setting the disposition that deletes, which is not a question. Where a
/// provider knows which directory a program runs from, the live-process veto spares it before this
/// is asked.</para>
/// </summary>
internal static class DeletionProbe
{
    /// <param name="extendedPath">The file, in the extended-length form §6.3 requires.</param>
    public static RefusalReason? Probe(string extendedPath)
    {
        using var handle = EntryDeletion.Open(extendedPath);

        return handle.IsInvalid ? RefusalReasons.OfWin32Error(Marshal.GetLastPInvokeError()) : null;
    }
}
