using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Deletes one file, or one empty directory, by its path through a single handle, read-only or not.
///
/// <para><b>Why not <see cref="File.Delete"/> and <see cref="Directory.Delete(string)"/>.</b> Windows
/// refuses them a read-only entry, so deleting one through them takes a second open to clear its bit.
/// Go marks its whole module cache read-only and Git marks its objects read-only, so a clean meets
/// whole trees of them. Measured on one machine through the benchmark's removal route, 100,000
/// read-only files took 6.7 s this way against 12.1 s cleared first, and the same files writable
/// 6.2 s either way: for an ordinary file this makes the calls <c>DeleteFileW</c> makes.</para>
///
/// <para><b>What the runtime would do, everywhere else.</b> Each refusal throws the exception
/// <see cref="File.Delete"/> or <see cref="Directory.Delete(string)"/> throws for the same error,
/// type and <see cref="Exception.HResult"/> alike, because the callers and
/// <see cref="RefusalReasons.Of"/> read nothing else. The entries this open would treat differently
/// from Windows' own calls go to those calls instead: see <see cref="RouteFor"/>. So does an entry on
/// a file system without the one-call disposition, as <c>DeleteFileW</c> falls back there.</para>
///
/// <para><b>What a refusal changes.</b> On the one-call route, nothing: the bit is never touched. On
/// the runtime's route a refused read-only entry has its bit cleared for one more try, which is the
/// only way that route deletes one, and set again where that try is refused too.</para>
/// </summary>
internal static class EntryDeletion
{
    private const uint Delete = 0x0001_0000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;

    /// <summary>The bit <c>IsReparseTagNameSurrogate</c> tests: the tag names another place in the namespace.</summary>
    private const uint NameSurrogate = 0x2000_0000;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction, or a folder a volume is mounted at.</summary>
    private const uint MountPoint = 0xA000_0003;

    /// <summary>
    /// Opens the entry at <paramref name="extendedPath"/> for deletion, as <c>DeleteFileW</c> opens
    /// one: delete access and attribute reads, sharing everything, the reparse point itself rather
    /// than what it names, and a directory as readily as a file. An invalid handle, with the error
    /// set, where Windows refused.
    /// </summary>
    /// <param name="extendedPath">The entry, in the extended-length form §6.3 requires.</param>
    public static SafeFileHandle Open(string extendedPath) =>
        FileInformation.CreateFile(
            extendedPath,
            Delete | FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            templateFile: 0);

    /// <summary>
    /// Deletes the file, or the link to a file, at <paramref name="extendedPath"/>, throwing what
    /// <see cref="File.Delete"/> throws: nothing where no file is there, and an exception where
    /// Windows refused.
    /// </summary>
    public static void DeleteFile(string extendedPath) => Remove(extendedPath, DeletedAs.File);

    /// <summary>
    /// Deletes the empty directory, or the link to a directory, at <paramref name="extendedPath"/>,
    /// throwing what <see cref="Directory.Delete(string)"/> throws.
    /// </summary>
    public static void DeleteDirectory(string extendedPath) => Remove(extendedPath, DeletedAs.Directory);

    /// <summary>
    /// How an entry Windows described as <paramref name="described"/> is deleted when it was asked for
    /// as <paramref name="asked"/>. <paramref name="described"/> is null where Windows would not describe it.
    /// </summary>
    internal static DeletionRoute RouteFor(FileInformation.FileAttributeTagInfo? described, DeletedAs asked)
    {
        // Nothing here can say what it is, so Windows' own call decides.
        if (described is not { } info)
        {
            return DeletionRoute.Runtime;
        }

        var attributes = (FileAttributes)info.FileAttributes;

        if (attributes.HasFlag(FileAttributes.Directory) != (asked is DeletedAs.Directory))
        {
            return DeletionRoute.WrongKind;
        }

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return DeletionRoute.OneCall;
        }

        // A cloud placeholder, a deduplicated file and their kind are handed by Windows' own call to
        // the filter that owns them, which this open, made on the reparse point itself, would not do.
        // A mount point goes there too: Windows' own call tells the mount manager when a folder a
        // volume is mounted at goes, and a junction shares the tag.
        return (info.ReparseTag & NameSurrogate) == 0 || info.ReparseTag == MountPoint
            ? DeletionRoute.Runtime
            : DeletionRoute.OneCall;
    }

    private static void Remove(string extendedPath, DeletedAs asked)
    {
        FileAttributes? attributes;

        using (var handle = Open(extendedPath))
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();

                // File.Delete's own answer: a name with nothing at it is already the outcome.
                if (error == Win32Error.FileNotFound && asked is DeletedAs.File)
                {
                    return;
                }

                throw Refusal(error, extendedPath, asked);
            }

            FileInformation.FileAttributeTagInfo? described =
                FileInformation.TryAttributeTag(handle, out var info) ? info : null;
            attributes = described is { } known ? (FileAttributes)known.FileAttributes : null;

            switch (RouteFor(described, asked))
            {
                case DeletionRoute.WrongKind:
                    // What each call answers the other kind with: a folder refuses the open for a file,
                    // and a file is not a directory.
                    throw Refusal(asked is DeletedAs.Directory ? Win32Error.NotADirectory : Win32Error.AccessDenied, extendedPath, asked);

                case DeletionRoute.OneCall:
                    var refused = HandleDeletion.TryDeleteNow(handle);

                    if (refused == 0)
                    {
                        return;
                    }

                    if (!HandleDeletion.IsUnsupported(refused))
                    {
                        throw Refusal(refused, extendedPath, asked);
                    }

                    break;
            }
        }

        // Closed first, so the runtime's own open is not refused by this one.
        ByTheRuntime(extendedPath, asked, attributes);
    }

    /// <summary>
    /// Deletes the entry through the runtime's call, clearing a read-only bit for one more try where
    /// the first was refused, and setting it again where that try is refused too.
    /// </summary>
    private static void ByTheRuntime(string extendedPath, DeletedAs asked, FileAttributes? attributes)
    {
        try
        {
            RuntimeDelete(extendedPath, asked);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            && (attributes ?? ReadAttributes(extendedPath)) is { } had
            && had.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(extendedPath, had & ~FileAttributes.ReadOnly);

            try
            {
                RuntimeDelete(extendedPath, asked);
            }
            catch (Exception again) when (again is UnauthorizedAccessException or IOException)
            {
                // The second answer is the one that counts: a read-only file something holds open is
                // refused first for the bit and then for the handle. The bit goes back, so the entry
                // left standing keeps the attributes it had.
                Restore(extendedPath, had);
                throw;
            }
        }
    }

    private static void RuntimeDelete(string extendedPath, DeletedAs asked)
    {
        switch (asked)
        {
            case DeletedAs.Directory:
                Directory.Delete(extendedPath, recursive: false);
                break;

            default:
                File.Delete(extendedPath);
                break;
        }
    }

    private static FileAttributes? ReadAttributes(string extendedPath) =>
        FileAttributeRead.Read(extendedPath, out var attributes) == 0 ? attributes : null;

    private static void Restore(string extendedPath, FileAttributes had)
    {
        try
        {
            File.SetAttributes(extendedPath, had);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone since, or no longer writable: either way the refusal being thrown is the answer the
            // caller needs, and this one would hide it.
        }
    }

    /// <summary>
    /// The exception the runtime's call throws for <paramref name="error"/>. A directory reads a
    /// missing entry as a missing path, and an access refusal as a bare <see cref="IOException"/>,
    /// which <see cref="Execution.DirectoryDeletion"/> relies on.
    /// </summary>
    private static Exception Refusal(int error, string path, DeletedAs asked) => (error, asked) switch
    {
        (Win32Error.FileNotFound or Win32Error.PathNotFound, DeletedAs.Directory) =>
            new DirectoryNotFoundException(Message(Win32Error.PathNotFound, path)),
        (Win32Error.AccessDenied, DeletedAs.Directory) => new IOException(Message(error, path)),
        (Win32Error.FileNotFound, _) => new FileNotFoundException(Message(error, path), path),
        (Win32Error.PathNotFound, _) => new DirectoryNotFoundException(Message(error, path)),
        (Win32Error.AccessDenied, _) => new UnauthorizedAccessException(Message(error, path)),
        (Win32Error.NameTooLong, _) => new PathTooLongException(Message(error, path)),
        _ => new IOException(Message(error, path), HResultOf(error)),
    };

    private static string Message(int error, string path) =>
        $"{Marshal.GetPInvokeErrorMessage(error)} : '{LongPath.Display(path)}'";

    /// <summary><c>HRESULT_FROM_WIN32</c>, the code the runtime puts on an <see cref="IOException"/>.</summary>
    private static int HResultOf(int error) => unchecked((int)0x8007_0000) | (error & 0xFFFF);
}
