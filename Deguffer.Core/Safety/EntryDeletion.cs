using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Deletes one file, or one empty directory, by its path through a single handle, read-only or not.
///
/// <para><b>Why not <see cref="File.Delete"/> and <see cref="Directory.Delete(string)"/>.</b> Windows
/// refuses them a read-only entry, so a removal cleared the bit through a second open first. Go marks
/// its whole module cache read-only and Git marks its objects read-only, so a clean meets whole trees
/// of them. Measured on one machine, a removal of 100,000 read-only files took 12.1 s against 5.9 s
/// for the same files writable, and one open with the disposition <see cref="HandleDeletion"/> sets
/// deleted 50,000 read-only files in 1.88 s against 5.21 s. For an ordinary file this makes the calls
/// <c>DeleteFileW</c> makes, and was timed level with it.</para>
///
/// <para><b>What the runtime would do, everywhere else.</b> Each refusal throws the exception
/// <see cref="File.Delete"/> or <see cref="Directory.Delete(string)"/> throws for the same error,
/// type and <see cref="Exception.HResult"/> alike, because the callers and
/// <see cref="RefusalReasons.Of"/> read nothing else. The entries this open would treat differently
/// from Windows' own calls go to those calls instead: see <see cref="RouteFor"/>. So does an entry on
/// a file system without the one-call disposition, as <c>DeleteFileW</c> falls back there.</para>
///
/// <para><b>A refusal changes nothing.</b> Clearing the bit first left a read-only file that Windows
/// then refused with its bit cleared. On the one-call route the bit is never touched. On the
/// runtime's route a refused read-only entry has its bit cleared for one more try, which is the only
/// way that route deletes one, and set again where that try is refused too.</para>
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
    public static void DeleteFile(string extendedPath) => Remove(extendedPath, asDirectory: false);

    /// <summary>
    /// Deletes the empty directory, or the link to a directory, at <paramref name="extendedPath"/>,
    /// throwing what <see cref="Directory.Delete(string)"/> throws.
    /// </summary>
    public static void DeleteDirectory(string extendedPath) => Remove(extendedPath, asDirectory: true);

    /// <summary>
    /// How an entry Windows described as <paramref name="described"/> is deleted when it was asked for
    /// as a directory or as a file, or null where Windows would not describe it.
    /// </summary>
    internal static DeletionRoute RouteFor(FileInformation.FileAttributeTagInfo? described, bool asDirectory)
    {
        // Nothing here can say what it is, so Windows' own call decides, as it did before.
        if (described is not { } info)
        {
            return DeletionRoute.Runtime;
        }

        var attributes = (FileAttributes)info.FileAttributes;

        if (attributes.HasFlag(FileAttributes.Directory) != asDirectory)
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

    private static void Remove(string extendedPath, bool asDirectory)
    {
        FileAttributes? attributes;

        using (var handle = Open(extendedPath))
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();

                // File.Delete's own answer: a name with nothing at it is already the outcome.
                if (error == Win32Error.FileNotFound && !asDirectory)
                {
                    return;
                }

                throw Refusal(error, extendedPath, asDirectory);
            }

            FileInformation.FileAttributeTagInfo? described =
                FileInformation.TryAttributeTag(handle, out var info) ? info : null;
            attributes = described is { } known ? (FileAttributes)known.FileAttributes : null;

            switch (RouteFor(described, asDirectory))
            {
                case DeletionRoute.WrongKind:
                    // What each call answers the other kind with: a folder refuses the open for a file,
                    // and a file is not a directory.
                    throw Refusal(asDirectory ? Win32Error.NotADirectory : Win32Error.AccessDenied, extendedPath, asDirectory);

                case DeletionRoute.OneCall:
                    var refused = HandleDeletion.TryDeleteNow(handle);

                    if (refused == 0)
                    {
                        return;
                    }

                    if (!HandleDeletion.IsUnsupported(refused))
                    {
                        throw Refusal(refused, extendedPath, asDirectory);
                    }

                    break;
            }
        }

        // Closed first, so the runtime's own open is not refused by this one.
        ByTheRuntime(extendedPath, asDirectory, attributes);
    }

    /// <summary>
    /// Deletes the entry through the runtime's call, clearing a read-only bit for one more try where
    /// the first was refused, and setting it again where that try is refused too.
    /// </summary>
    private static void ByTheRuntime(string extendedPath, bool asDirectory, FileAttributes? attributes)
    {
        try
        {
            RuntimeDelete(extendedPath, asDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            && (attributes ?? ReadAttributes(extendedPath)) is { } had
            && had.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(extendedPath, had & ~FileAttributes.ReadOnly);

            try
            {
                RuntimeDelete(extendedPath, asDirectory);
            }
            catch (Exception again) when (again is UnauthorizedAccessException or IOException)
            {
                // The second answer is the one that counts: a read-only file something holds open is
                // refused first for the bit and then for the handle. The bit goes back, so the entry
                // left standing is as it was.
                Restore(extendedPath, had);
                throw;
            }
        }
    }

    private static void RuntimeDelete(string extendedPath, bool asDirectory)
    {
        if (asDirectory)
        {
            Directory.Delete(extendedPath, recursive: false);
        }
        else
        {
            File.Delete(extendedPath);
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
    private static Exception Refusal(int error, string path, bool asDirectory) => error switch
    {
        Win32Error.FileNotFound or Win32Error.PathNotFound when asDirectory =>
            new DirectoryNotFoundException(Message(Win32Error.PathNotFound, path)),
        Win32Error.AccessDenied when asDirectory => new IOException(Message(error, path)),
        Win32Error.FileNotFound => new FileNotFoundException(Message(error, path), path),
        Win32Error.PathNotFound => new DirectoryNotFoundException(Message(error, path)),
        Win32Error.AccessDenied => new UnauthorizedAccessException(Message(error, path)),
        Win32Error.NameTooLong => new PathTooLongException(Message(error, path)),
        _ => new IOException(Message(error, path), HResultOf(error)),
    };

    private static string Message(int error, string path) =>
        $"{Marshal.GetPInvokeErrorMessage(error)} : '{LongPath.Display(path)}'";

    /// <summary><c>HRESULT_FROM_WIN32</c>, the code the runtime puts on an <see cref="IOException"/>.</summary>
    private static int HResultOf(int error) => unchecked((int)0x8007_0000) | (error & 0xFFFF);
}
