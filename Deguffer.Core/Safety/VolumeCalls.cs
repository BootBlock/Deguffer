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
    /// </summary>
    internal static string? MountPointOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(LabelLength * sizeof(char));

        try
        {
            return GetVolumePathName(path, buffer, LabelLength)
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Fixed, removable, network and so on, for a volume reached at <paramref name="mountPoint"/>.
    /// <see cref="DriveType"/>'s members are the <c>DRIVE_</c> constants this returns.
    /// </summary>
    internal static DriveType KindOf(string mountPoint) => (DriveType)GetDriveType(mountPoint);

    /// <summary>
    /// What the volume at <paramref name="mountPoint"/> is called and what it says it supports, or
    /// null and <see cref="VolumeFeatures.None"/> where it would not say.
    ///
    /// <para>One call for both, because Windows answers both from one. Splitting them would ask a
    /// volume that has already refused once to refuse again, and would let the label and the flags
    /// come from two readings a moment apart.</para>
    /// </summary>
    internal static (string? Label, VolumeFeatures Features) InformationOf(string mountPoint)
    {
        var buffer = Marshal.AllocHGlobal(LabelLength * sizeof(char));

        try
        {
            if (!GetVolumeInformation(mountPoint, buffer, LabelLength, out _, out _, out var flags, IntPtr.Zero, 0))
            {
                return (null, VolumeFeatures.None);
            }

            var label = Marshal.PtrToStringUni(buffer);

            // Null rather than empty, so "unlabelled" is one case at every caller instead of two.
            return (string.IsNullOrWhiteSpace(label) ? null : label, (VolumeFeatures)flags);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
