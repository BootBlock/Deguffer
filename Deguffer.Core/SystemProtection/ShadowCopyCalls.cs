using System.Runtime.InteropServices;

namespace Deguffer.Core.SystemProtection;

/// <summary>
/// The Volume Shadow Copy service's own listing of every shadow copy on the machine, from the SDK's
/// <c>vsbackup.h</c>: <c>IVssBackupComponents::Query</c> in the context that covers every kind
/// (<c>VSS_CTX_ALL</c>), which is the documented route.
///
/// <para><b>Through the interface's own table rather than a <c>ComImport</c> declaration.</b> The
/// object comes from a library function rather than from COM, and the calls used are the 5th, 35th and
/// 43rd of 50. Each slot is named here with its place in <c>vsbackup.h</c>, so the three can be checked
/// against the header one by one.</para>
///
/// <para><b>Not <c>Win32_ShadowCopy</c></b>, which Microsoft documents as supported on no client
/// Windows, and not <c>IVssSnapshotMgmt::QuerySnapshotsByVolume</c>, which it reserves for system
/// use.</para>
/// </summary>
internal static unsafe class ShadowCopyCalls
{
    private const int S_OK = 0;

    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>VSS_E_OBJECT_NOT_FOUND</c>: there is no shadow copy to list.</summary>
    private const int ObjectNotFound = unchecked((int)0x80042308);

    /// <summary><c>COINIT_MULTITHREADED</c>.</summary>
    private const uint MultiThreaded = 0;

    /// <summary><c>VSS_CTX_ALL</c>.</summary>
    private const int AllContexts = unchecked((int)0xFFFFFFFF);

    /// <summary><c>VSS_OBJECT_NONE</c>, which with an empty identifier asks for everything.</summary>
    private const int NoObject = 1;

    /// <summary><c>VSS_OBJECT_SNAPSHOT</c>.</summary>
    private const int SnapshotObject = 3;

    /// <summary><c>IUnknown::Release</c>.</summary>
    private const int ReleaseSlot = 2;

    /// <summary><c>InitializeForBackup</c>, the third method after <c>IUnknown</c>'s three.</summary>
    private const int InitializeForBackupSlot = 3 + 2;

    /// <summary><c>SetContext</c>, the 33rd.</summary>
    private const int SetContextSlot = 3 + 32;

    /// <summary><c>Query</c>, the 41st.</summary>
    private const int QuerySlot = 3 + 40;

    /// <summary><c>IVssEnumObject::Next</c>, the first after <c>IUnknown</c>'s.</summary>
    private const int NextSlot = 3;

    /// <summary>
    /// <c>VSS_OBJECT_PROP</c> is a four-byte type and a union holding 64-bit fields, so the union starts
    /// eight bytes in on every architecture this app is built for.
    /// </summary>
    private const int UnionOffset = 8;

    /// <summary>Room for <c>VSS_OBJECT_PROP</c> on any architecture, with some to spare.</summary>
    private const int PropertySize = 256;

    public static ShadowCopyListing List()
    {
        // The service is reached through COM, and nothing in a call through a function pointer makes the
        // runtime join this thread to an apartment the way a COM interop call would. A thread already in
        // one answers RPC_E_CHANGED_MODE, and is used as it is.
        var joined = CoInitializeEx(IntPtr.Zero, MultiThreaded) >= 0;

        try
        {
            return Listed();
        }
        finally
        {
            if (joined)
            {
                CoUninitialize();
            }
        }
    }

    private static ShadowCopyListing Listed()
    {
        IntPtr backup;
        var hr = CreateVssBackupComponentsInternal(&backup);

        if (hr != S_OK)
        {
            return Unanswered(hr);
        }

        try
        {
            hr = Call(backup, InitializeForBackupSlot, IntPtr.Zero);

            if (hr != S_OK)
            {
                return Unanswered(hr);
            }

            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(backup, SetContextSlot))(backup, AllContexts);

            if (hr != S_OK)
            {
                return Unanswered(hr);
            }

            IntPtr enumeration;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid, int, int, IntPtr*, int>)Slot(backup, QuerySlot))(
                backup, Guid.Empty, NoObject, SnapshotObject, &enumeration);

            // A machine with no shadow copy answers with this rather than an empty enumeration.
            if (hr == ObjectNotFound || (hr >= 0 && enumeration == IntPtr.Zero))
            {
                return ShadowCopyListing.Of([]);
            }

            if (hr < 0)
            {
                return Unanswered(hr);
            }

            try
            {
                return Read(enumeration);
            }
            finally
            {
                Release(enumeration);
            }
        }
        finally
        {
            Release(backup);
        }
    }

    /// <summary>
    /// Every object the enumeration holds. One that stops answering part way fails the listing: a copy
    /// missing from it is one the clean would not know to prove standing.
    /// </summary>
    private static ShadowCopyListing Read(IntPtr enumeration)
    {
        List<ShadowCopy> copies = [];
        var property = Marshal.AllocCoTaskMem(PropertySize);
        var next = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint*, int>)Slot(enumeration, NextSlot);

        try
        {
            while (true)
            {
                new Span<byte>((void*)property, PropertySize).Clear();
                uint fetched;
                var hr = next(enumeration, 1, property, &fetched);

                if (hr < 0)
                {
                    return Unanswered(hr);
                }

                if (fetched == 0)
                {
                    return ShadowCopyListing.Of(copies);
                }

                if (Marshal.ReadInt32(property) != SnapshotObject)
                {
                    continue;
                }

                var snapshot = property + UnionOffset;

                try
                {
                    copies.Add(Copy(snapshot));
                }
                finally
                {
                    VssFreeSnapshotPropertiesInternal(snapshot);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(property);
        }
    }

    /// <summary>
    /// One <c>VSS_SNAPSHOT_PROP</c>, read by offset for the reason
    /// <see cref="Exploring.Hidden.ShadowStorageCalls"/> reads its union by offset: six pointers sit in the
    /// middle of it, and every field after them moves with the pointer size.
    /// </summary>
    private static ShadowCopy Copy(IntPtr snapshot)
    {
        var id = Marshal.PtrToStructure<Guid>(snapshot);

        // The two identifiers and the count, then six pointers aligned to their own size.
        var pointers = Align(16 + 16 + 4, IntPtr.Size);
        var volume = Marshal.PtrToStringUni(Marshal.ReadIntPtr(snapshot, pointers + IntPtr.Size)) ?? string.Empty;
        var provider = pointers + (6 * IntPtr.Size);
        var attributes = provider + 16;
        var timestamp = Align(attributes + 4, 8);

        return new ShadowCopy(
            id,
            Marshal.PtrToStructure<Guid>(snapshot + provider),
            Marshal.ReadInt32(snapshot, attributes),
            volume,
            Created(Marshal.ReadInt64(snapshot, timestamp)));
    }

    /// <summary>
    /// The creation time, which the system provider records as a <c>FILETIME</c>. A provider is free to
    /// record another, so a value that does not read as one is the earliest date rather than a failure:
    /// the time names a copy for the reader, and identifies nothing.
    /// </summary>
    private static DateTime Created(long fileTime) =>
        fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc()
            ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime()
            : DateTime.MinValue;

    private static int Align(int offset, int boundary) => (offset + boundary - 1) & ~(boundary - 1);

    private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    private static int Call(IntPtr instance, int slot, IntPtr argument) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(instance, slot))(instance, argument);

    private static void Release(IntPtr instance) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, ReleaseSlot))(instance);

    /// <summary>
    /// The listing refuses a caller that is neither an administrator nor a backup operator, which the
    /// documentation for <c>Query</c> states.
    /// </summary>
    private static ShadowCopyListing Unanswered(int hr) =>
        hr == AccessDenied
            ? ShadowCopyListing.Refused
            : ShadowCopyListing.Failed($"The Volume Shadow Copy service did not list its shadow copies (error 0x{hr:X8}).");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    /// <summary><c>CreateVssBackupComponents</c>, which the header defines as a call to this export.</summary>
    [DllImport("VssApi.dll")]
    private static extern int CreateVssBackupComponentsInternal(IntPtr* backup);

    /// <summary>Frees the strings the service allocated in one <c>VSS_SNAPSHOT_PROP</c>.</summary>
    [DllImport("VssApi.dll")]
    private static extern void VssFreeSnapshotPropertiesInternal(IntPtr snapshot);
}
