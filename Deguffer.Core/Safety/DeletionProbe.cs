using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Whether Windows would let this process delete a file, asked the only way that gives the answer a
/// deletion would get.
///
/// <para><b>The security descriptor cannot answer it.</b> The case that made this necessary was a
/// file whose DACL granted its owner full control, with no deny entry and no integrity label, which
/// still refused <c>DELETE</c> to its owner and to an administrator: a filter driver below the ACL
/// was refusing the open. <c>AccessCheck</c> against the descriptor would have said yes. So the
/// file is opened for deletion exactly as <c>DeleteFileW</c> opens it, and closed again at once
/// without the disposition that would delete it.</para>
///
/// <para><b>It is not free of side effects, and the callers are chosen for that.</b> While the
/// handle is open, another program's open that does not share delete access fails. The handle lives
/// for microseconds and a refused open creates none, which is why this is asked only inside the
/// places a previous clean found Windows refusing, where most files usually still refuse — see
/// <see cref="Execution.RefusalCheck"/>.</para>
///
/// <para>An executable something is running from opens for deletion and then refuses the deletion
/// itself, so this answers that it would go. The removal reports it refused, and the next check
/// counts it again. Such a directory is the live-process veto's to spare rather than this probe's to
/// see.</para>
/// </summary>
internal static partial class DeletionProbe
{
    private const uint Delete = 0x0001_0000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;

    /// <param name="extendedPath">The file, in the extended-length form §6.3 requires.</param>
    public static RefusalReason? Probe(string extendedPath)
    {
        using var handle = CreateFile(
            extendedPath,
            Delete | FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            templateFile: 0);

        return handle.IsInvalid ? RefusalReasons.OfWin32Error(Marshal.GetLastPInvokeError()) : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
