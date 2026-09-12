using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads the machine-wide figures: <c>GetPerformanceInfo</c>, which Windows documents, and the memory
/// lists, which it does not and which <see cref="MemoryListCheck.Accept"/> therefore decides about
/// before they are used.
/// </summary>
internal sealed partial class SystemMemoryReader
{
    /// <summary>The page counts, kept between reads for the reason <see cref="ProcessTableReader"/> keeps its buffer.</summary>
    private readonly nuint[] _counts = SystemInformation.PinnedArray<nuint>(MemoryLists.CountLength);

    public SystemMemory Read()
    {
        if (!GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var page = (long)info.PageSize;
        var total = (long)info.PhysicalTotal * page;
        var available = (long)info.PhysicalAvailable * page;

        var length = _counts.Length * IntPtr.Size;
        var status = SystemInformation.NtQuerySystemInformation(
            SystemInformation.MemoryListInformation, SystemInformation.AddressOf(_counts), length, out var returned);

        var (lists, state) = MemoryListCheck.Accept(status == 0 && returned == length, _counts, page, available, total);

        return new SystemMemory(
            PhysicalTotal: total,
            Available: available,
            CommitCharge: (long)info.CommitTotal * page,
            CommitLimit: (long)info.CommitLimit * page,
            SystemCache: (long)info.SystemCache * page,
            PagedPool: (long)info.KernelPaged * page,
            NonPagedPool: (long)info.KernelNonpaged * page,
            Lists: lists,
            ListState: state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(out PerformanceInformation information, uint size);
}
