using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deguffer.Core.Execution;

/// <summary>
/// The handlers Windows registers under <c>VolumeCaches</c>, created the way Disk Cleanup creates
/// them: from the class the registration names, through the second handler interface where the class
/// has it and the first where it does not.
///
/// <para>Stateless, so one instance serves the process (G5).</para>
/// </summary>
internal sealed class WindowsVolumeCacheLoader : IVolumeCacheLoader
{
    public static WindowsVolumeCacheLoader Default { get; } = new();

    private const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    private WindowsVolumeCacheLoader()
    {
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public bool IsRegistered(string handler) => ClassOf(handler) is not null;

    public LoadedVolumeCache? Load(string handler, out int failure)
    {
        if (ClassOf(handler) is not { } classId
            || Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}") is not { } key)
        {
            failure = DiskCleanupNative.REGDB_E_CLASSNOTREG;
            return null;
        }

        var cacheId = DiskCleanupNative.VolumeCache2Id;
        var created = DiskCleanupNative.CoCreateInstance(
            ref classId, IntPtr.Zero, DiskCleanupNative.InProcessServer, ref cacheId, out var instance);

        if (created < 0)
        {
            cacheId = DiskCleanupNative.VolumeCacheId;
            created = DiskCleanupNative.CoCreateInstance(
                ref classId, IntPtr.Zero, DiskCleanupNative.InProcessServer, ref cacheId, out instance);
        }

        if (created < 0)
        {
            key.Dispose();
            failure = created;
            return null;
        }

        failure = DiskCleanupNative.S_OK;

        return new LoadedVolumeCache(instance, key.Handle.DangerousGetHandle(), () =>
        {
            Marshal.FinalReleaseComObject(instance);
            key.Dispose();
        });
    }

    /// <summary>
    /// The class a registration names, from the registration key's default value, in this process's
    /// view of the registry. Null where the key is missing or names nothing that parses as a class. The
    /// view is the point: a 32-bit Deguffer on 64-bit Windows sees the 32-bit registrations, and a
    /// class with no 32-bit server cannot be loaded into it.
    /// </summary>
    private static Guid? ClassOf(string handler)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}");

        if (key?.GetValue(null) is not string text || !Guid.TryParse(text, out var classId))
        {
            return null;
        }

        using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId:B}\InprocServer32");

        return server?.GetValue(null) is string { Length: > 0 } ? classId : null;
    }
}
