using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Hidden;

/// <summary>
/// <c>GetDiskSpaceInformation</c>, the one documented call that states the storage reserve's size.
///
/// <para>The others refuse or say less. <c>fsutil storagereserve query</c> refuses an unelevated
/// process, and the DISM call and its cmdlet report only whether reserved storage is on. This one
/// answers an ordinary process, which was observed on Windows 11 and is why the reserve is drawn
/// without elevation while the shadow copy storage is not.</para>
/// </summary>
internal static partial class DiskSpaceCalls
{
    private const int S_OK = 0;

    /// <summary>The reserved storage on the volume at <paramref name="volumeRoot"/>.</summary>
    /// <param name="volumeRoot">A mount point ending in a separator, which the call requires.</param>
    public static ReservedStorage ReserveOn(string volumeRoot)
    {
        if (GetDiskSpaceInformation(volumeRoot, out var space) != S_OK)
        {
            return new ReservedStorage(Statement.NotStated);
        }

        var cluster = (long)space.SectorsPerAllocationUnit * space.BytesPerSector;

        return new ReservedStorage(Statement.Stated, (long)space.VolumeStorageReserveAllocationUnits * cluster);
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetDiskSpaceInformationW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetDiskSpaceInformation(string rootPath, out DiskSpaceInformation information);

    /// <summary><c>DISK_SPACE_INFORMATION</c>, in <c>fileapi.h</c>'s order. Every count is in clusters.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DiskSpaceInformation
    {
        public ulong ActualTotalAllocationUnits;
        public ulong ActualAvailableAllocationUnits;
        public ulong ActualPoolUnavailableAllocationUnits;
        public ulong CallerTotalAllocationUnits;
        public ulong CallerAvailableAllocationUnits;
        public ulong CallerPoolUnavailableAllocationUnits;
        public ulong UsedAllocationUnits;
        public ulong TotalReservedAllocationUnits;
        public ulong VolumeStorageReserveAllocationUnits;
        public ulong AvailableCommittedAllocationUnits;
        public ulong PoolAvailableAllocationUnits;
        public uint SectorsPerAllocationUnit;
        public uint BytesPerSector;
    }
}
