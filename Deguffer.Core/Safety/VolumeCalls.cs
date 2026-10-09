using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// The Win32 volume calls, which are the only route to a volume that is not mounted under a drive
/// letter.
///
/// <para><c>DriveInfo</c> is built on <c>GetLogicalDrives</c>, and its constructor normalises
/// whatever it is handed down to a drive root — so <c>new DriveInfo(@"C:\Mount\")</c> describes
/// <c>C:</c> rather than the volume mounted at that folder, and <c>GetDrives</c> never names such a
/// volume at all. Every reading a folder-mounted volume has to answer for itself therefore comes
/// from here.</para>
///
/// <para>Each call answers rather than throws: a volume that will not say returns false, which
/// <see cref="VolumeInventory"/> records as "was not asked" in the same shape as a volume that was
/// never reachable. That is why nothing here catches anything.</para>
///
/// <para>Internal because nothing outside Core has business calling Win32 directly, and because a
/// caller that wants a volume wants <see cref="IVolumeInventory"/>, which is the seam a test can
/// stand in for.</para>
/// </summary>
internal static partial class VolumeCalls
{
    /// <summary>
    /// The buffer <c>FindFirstVolumeW</c> documents as sufficient: a volume name is
    /// <c>\\?\Volume{GUID}\</c>, which is 49 characters and a terminator.
    /// </summary>
    private const int VolumeNameLength = 50;

    /// <summary><c>MAX_PATH</c> and a terminator, which is the longest label NTFS will store.</summary>
    private const int LabelLength = 261;

    private const int ErrorMoreData = 234;

    /// <summary>
    /// The longest path an extended-length name can be, 32,767 characters, and a terminator. What
    /// <see cref="MountPointOf"/> allocates, since its answer can be longer than its question.
    /// </summary>
    private const uint LongestPath = 32_768;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// Every volume this machine has, by the <c>\\?\Volume{GUID}\</c> name the mount-point lookup
    /// takes. Empty where the enumeration would not start.
    ///
    /// <para><b>Local volumes only.</b> A mapped network drive is a letter standing for somewhere
    /// else rather than a volume of this machine, so it is not named here —
    /// <see cref="VolumeInventory"/> adds the letters this enumeration did not claim.</para>
    /// </summary>
    internal static IEnumerable<string> Names()
    {
        var buffer = Marshal.AllocHGlobal(VolumeNameLength * sizeof(char));

        try
        {
            var find = FindFirstVolume(buffer, VolumeNameLength);

            if (find == InvalidHandle)
            {
                yield break;
            }

            try
            {
                do
                {
                    if (Marshal.PtrToStringUni(buffer) is { Length: > 0 } name)
                    {
                        yield return name;
                    }
                }
                while (FindNextVolume(find, buffer, VolumeNameLength));
            }
            finally
            {
                // Reached on an abandoned enumeration as well as an exhausted one, because a
                // foreach that breaks early disposes the iterator and runs this.
                FindVolumeClose(find);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Every path <paramref name="volumeName"/> is reachable at, each ending in a separator, in
    /// whatever order Windows gives them. Empty where the volume is mounted nowhere — a recovery
    /// partition, or one whose letter was removed — which is a volume no path can name and so
    /// nothing can act on.
    ///
    /// <para>A volume can have several. A drive letter and a folder mount point are the same
    /// arrangement to Windows, and one volume may carry any number of each.</para>
    /// </summary>
    internal static IReadOnlyList<string> MountPointsOf(string volumeName)
    {
        if (!GetVolumePathNamesForVolumeName(volumeName, IntPtr.Zero, 0, out var needed))
        {
            // ERROR_MORE_DATA is how the sizing call reports the length it wants, so it is the one
            // failure that is not a failure. Anything else is a volume that would not answer.
            if (Marshal.GetLastPInvokeError() != ErrorMoreData)
            {
                return [];
            }
        }

        if (needed == 0)
        {
            return [];
        }

        var length = (int)needed;
        var buffer = Marshal.AllocHGlobal(length * sizeof(char));

        try
        {
            if (!GetVolumePathNamesForVolumeName(volumeName, buffer, needed, out _))
            {
                return [];
            }

            var paths = new List<string>();

            // A multi-string: each path terminated, the set terminated again by an empty one.
            for (var offset = 0; offset < length;)
            {
                if (Marshal.PtrToStringUni(buffer + (offset * sizeof(char))) is not { Length: > 0 } path)
                {
                    break;
                }

                paths.Add(path);
                offset += path.Length + 1;
            }

            return paths;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The mount point of the volume holding <paramref name="path"/>, or null where Windows will
    /// not say — which is a path that names no volume of this machine, and a volume that is not
    /// there.
    ///
    /// <para>Answers for a path that does not exist, and for a file as readily as a directory,
    /// which is what separates it from asking the filesystem.</para>
    ///
    /// <para>§6.3: the path is normalised and extended before it crosses, and the buffer holds the
    /// longest path an extended-length name can be rather than <c>MAX_PATH</c>, where a fixed 260
    /// would answer null for the Node and NuGet trees §6.3 exists for. It is not sized from the
    /// path, because the answer can be longer than the question: the folder a volume is mounted at
    /// comes back with a separator it was asked without, and a path through a junction is answered
    /// with a mount point somewhere else entirely, and <see cref="VolumeRoot"/> decides what may be
    /// deleted from that answer. The answer
    /// comes back in display form, because that is what the rest of the app shows and hands on.</para>
    /// </summary>
    internal static string? MountPointOf(string path)
    {
        // Configured rather than Extended alone: it resolves a relative path, rejects one Windows
        // will not accept, and answers null instead of throwing — which is this method's own
        // contract for a path that names no volume.
        if (LongPath.Configured(path) is not { } qualified)
        {
            return null;
        }

        var extended = LongPath.Extended(qualified);
        var buffer = Marshal.AllocHGlobal((int)LongestPath * sizeof(char));

        try
        {
            return GetVolumePathName(extended, buffer, LongestPath)
                && Marshal.PtrToStringUni(buffer) is { Length: > 0 } mountPoint
                ? LongPath.Display(mountPoint)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The <c>\\?\Volume{GUID}\</c> name of the volume mounted at <paramref name="mountPoint"/>, which
    /// opens that volume wherever it is mounted, or null with the error where Windows will not say.
    /// <paramref name="mountPoint"/> ends in a separator, as every mount point here does.
    /// </summary>
    internal static string? VolumeNameOf(string mountPoint, out int error)
    {
        var buffer = Marshal.AllocHGlobal(VolumeNameLength * sizeof(char));

        try
        {
            if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, VolumeNameLength))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            error = 0;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The folder the drive letter at the start of <paramref name="driveRoot"/> stands for, where
    /// <c>subst</c> made it one, or null where it is a volume's own letter, a mapped share, or not a
    /// letter at all.
    ///
    /// <para>Windows names no volume for a letter standing for a folder below a volume's top: both
    /// <c>GetVolumePathName</c> and <c>GetVolumeNameForVolumeMountPoint</c> fail for it, so this is
    /// the only call that says where the letter leads.</para>
    /// </summary>
    internal static string? SubstituteOf(string driveRoot)
    {
        if (driveRoot is not [var letter, ':', ..] || !char.IsAsciiLetter(letter))
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)LongestPath * sizeof(char));

        try
        {
            return QueryDosDevice($"{letter}:", buffer, LongestPath) > 0
                ? Substitution(Marshal.PtrToStringUni(buffer))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The folder a DOS device target names, in display form, or null where it names a device
    /// rather than a folder. <c>subst</c> writes its target in the <c>\??\</c> namespace, as
    /// <c>\??\C:\Users\testuser</c> or <c>\??\UNC\server\share</c>, and a volume's own letter
    /// answers a device such as <c>\Device\HarddiskVolume3</c>.
    ///
    /// <para>A letter made for the top of a drive is written <c>\??\C:</c>, without the separator
    /// that makes it the root rather than a drive-relative path, so the separator is put back.</para>
    /// </summary>
    internal static string? Substitution(string? target)
    {
        if (target is not { Length: > 4 } || !target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return null;
        }

        var folder = target[4..];

        return LongPath.Configured(@"\\?\" + (folder is [_, ':'] ? folder + Path.DirectorySeparatorChar : folder));
    }

    /// <summary>
    /// Fixed, removable, network and so on, for a volume reached at <paramref name="mountPoint"/>.
    /// <see cref="DriveType"/>'s members are the <c>DRIVE_</c> constants this returns.
    /// </summary>
    internal static DriveType KindOf(string mountPoint) => (DriveType)GetDriveType(mountPoint);

    /// <summary>
    /// What the volume at <paramref name="mountPoint"/> is called, what it says it supports and the
    /// name of its file system, or null for each where it would not say.
    ///
    /// <para>One call for all three, because Windows answers them from one. Splitting them would ask
    /// a volume that has already refused once to refuse again, and would let the answers come from
    /// readings a moment apart.</para>
    /// </summary>
    /// <returns>The label, the flags and the file system's name, or null for each where the volume would not answer.</returns>
    internal static (string? Label, VolumeFeatures? Features, string? FileSystem) InformationOf(string mountPoint)
    {
        var label = Marshal.AllocHGlobal(LabelLength * sizeof(char));
        var fileSystem = Marshal.AllocHGlobal(LabelLength * sizeof(char));

        try
        {
            if (!GetVolumeInformation(mountPoint, label, LabelLength, out _, out _, out var flags, fileSystem, LabelLength))
            {
                return (null, null, null);
            }

            var named = Marshal.PtrToStringUni(label);
            var format = Marshal.PtrToStringUni(fileSystem);

            // Null rather than empty, so "unlabelled" is one case at every caller instead of two.
            return (
                string.IsNullOrWhiteSpace(named) ? null : named,
                (VolumeFeatures)flags,
                string.IsNullOrWhiteSpace(format) ? null : format);
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            Marshal.FreeHGlobal(fileSystem);
        }
    }

    /// <summary>
    /// Capacity and what is left of it for this user at <paramref name="mountPoint"/>, or null
    /// where the volume would not say.
    ///
    /// <para>The free figure is the one a quota allows rather than the raw free space, which is
    /// what <c>DriveInfo.AvailableFreeSpace</c> reported before this call replaced it. Every
    /// free-space figure in the app comes through here, so none of them can disagree.</para>
    /// </summary>
    internal static (long Total, long Free)? SpaceOf(string mountPoint) =>
        GetDiskFreeSpaceEx(mountPoint, out var free, out var total, out _)
            ? ((long)total, (long)free)
            : null;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    private static partial IntPtr FindFirstVolume(IntPtr volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextVolume(IntPtr findVolume, IntPtr volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindVolumeClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeClose(IntPtr findVolume);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetVolumePathNamesForVolumeNameW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathNamesForVolumeName(
        string volumeName, IntPtr volumePathNames, uint bufferLength, out uint returnLength);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetVolumePathNameW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, IntPtr volumePathName, uint bufferLength);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeNameForVolumeMountPointW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint, IntPtr volumeName, uint bufferLength);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryDosDeviceW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDevice(string deviceName, IntPtr targetPath, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer,
        uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
