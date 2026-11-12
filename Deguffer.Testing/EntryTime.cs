using System.ComponentModel;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// Dates a file or directory a test made, by both of the times Deguffer reads.
///
/// <para>The age column and the minimum-age guard both read the newer of an entry's creation and last
/// write. An entry a test has just made was created now, so setting its last write alone leaves it
/// dated now whatever the test meant. Setting both is how a test makes an entry as old as it says.</para>
/// </summary>
public static class EntryTime
{
    /// <summary>
    /// A FILETIME in the year 10000, one day past the last instant a <see cref="DateTime"/> can hold.
    /// NTFS stores whatever a program sets, up to the year 30828, so a listing can meet one.
    /// </summary>
    public static readonly long PastTheLastDateTime = DateTime.MaxValue.ToFileTimeUtc() + TimeSpan.TicksPerDay;

    /// <summary>Make <paramref name="path"/> created and last written at <paramref name="utc"/>.</summary>
    public static void Set(string path, DateTime utc) => Set(path, created: utc, written: utc);

    /// <summary>
    /// Make <paramref name="path"/> created at <paramref name="created"/> and last written at
    /// <paramref name="written"/>, for a test about which of the two an age is read from.
    /// </summary>
    public static void Set(string path, DateTime created, DateTime written)
    {
        var extended = LongPath.Extended(path);

        // A directory's own times matter to a provider that judges an empty folder by them, and
        // the File calls refuse a directory.
        if (Directory.Exists(extended))
        {
            Directory.SetCreationTimeUtc(extended, created);
            Directory.SetLastWriteTimeUtc(extended, written);
        }
        else
        {
            File.SetCreationTimeUtc(extended, created);
            File.SetLastWriteTimeUtc(extended, written);
        }
    }

    /// <summary>
    /// Make <paramref name="path"/>, a file or a directory, created and last written at the FILETIMEs
    /// given, for a time no <see cref="DateTime"/> can carry, such as <see cref="PastTheLastDateTime"/>.
    /// </summary>
    public static void SetFileTimes(string path, long created, long written)
    {
        // Backup semantics is what lets CreateFile open a directory as well as a file.
        using var handle = CreateFile(
            LongPath.Extended(path), WriteAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);

        if (handle.IsInvalid || !SetFileTime(handle, created, lastAccess: 0, written))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not date {path}.");
        }
    }

    private const uint WriteAttributes = 0x0100;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, in long created, nint lastAccess, in long written);
}
