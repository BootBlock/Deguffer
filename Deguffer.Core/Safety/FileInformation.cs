using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// What Windows says about a file or folder through a handle opened for its attributes alone, which
/// reads no data and so can neither download a cloud file nor be refused for want of read access.
/// Stateless apart from how it opens a handle, so one instance serves every caller (G5).
///
/// <para><b>One home for these calls.</b> Each caller that needed one declared its own, so the same
/// question had several answers waiting to drift apart. The final path is the first to move here;
/// what a handle says about a file's identity and its streams joins it as the duplicate search needs
/// them (<c>docs/todo/duplicates.md</c>, phase 2).</para>
/// </summary>
internal sealed unsafe partial class FileInformation
{
    public static FileInformation Default { get; } = new(OpenToResolve);

    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;

    /// <summary>The longest path Windows accepts in extended-length form.</summary>
    private const int LongestPath = 32_768;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>.</summary>
    private const uint FileNameNormalized = 0;

    private readonly Func<string, SafeFileHandle> _open;

    /// <param name="open">
    /// Opens the handle a path is resolved through, given the path in the form it reaches Windows.
    /// A test wraps <see cref="OpenToResolve"/> to see that form (§6.3): the call is a raw
    /// <c>CreateFileW</c>, and on a machine that allows long paths a deep folder opens whether or not
    /// the path carries the extended prefix, so only the form itself can show it was given.
    /// </param>
    internal FileInformation(Func<string, SafeFileHandle> open) => _open = open;

    /// <summary>
    /// Where <paramref name="path"/> really is once every link on the way to it, its own name
    /// included, is followed, in the extended form Windows answers with, or null where Windows would
    /// not open it. A substituted drive letter is followed too, because it is a link the object
    /// manager keeps.
    ///
    /// <para>Null says only that no answer was had: the path may be absent, or Windows may have
    /// refused to open it. A caller never reads it as absence.</para>
    /// </summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public string? FinalPath(string path)
    {
        using var handle = _open(LongPath.Extended(path));

        return handle.IsInvalid ? null : FinalPathOf(handle);
    }

    /// <summary>
    /// The normalised path of whatever <paramref name="handle"/> is open on, with its drive letter, or
    /// null where Windows would not say.
    /// </summary>
    public static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[LongestPath];

        fixed (char* chars = buffer)
        {
            var length = GetFinalPathNameByHandle(handle, chars, LongestPath, FileNameNormalized);

            return length is > 0 and < LongestPath ? new string(chars, 0, (int)length) : null;
        }
    }

    /// <summary>A handle that follows every link, for learning where a file or folder really is.</summary>
    /// <param name="extendedPath">The path in the extended form §6.3 requires.</param>
    internal static SafeFileHandle OpenToResolve(string extendedPath) =>
        CreateFile(extendedPath, FileReadAttributes, ShareAll, securityAttributes: 0, OpenExisting, BackupSemantics, templateFile: 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
