using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deguffer.Core.Execution;

/// <summary>
/// The real handlers, hosted the way Disk Cleanup hosts them: created from the class their
/// registration names, initialised with that registration's own key, then asked to measure and to
/// purge.
///
/// <para><b>The registration's key is handed over, not only its name.</b> One class,
/// <c>setupcln.dll</c>, serves <em>Previous Installations</em>, <em>Temporary Setup Files</em>,
/// <em>Windows ESD installation files</em> and three more, and what it reads to tell which it is being
/// are the values on the key: <c>SetupPrevInst</c> and <c>RemoveUninstall</c> on one,
/// <c>SetupDirectories</c> on another. A host that passed any other key would get a different
/// cleanup from the same class.</para>
///
/// <para><b>Nothing on the handler's side is documented beyond the interface.</b> Microsoft states what
/// Disk Cleanup offers and that the previous installation cannot be brought back, and nothing about
/// what <c>setupcln.dll</c> deletes. That is the reason the plan measures every directory the
/// registration names before and after, and the reason a run holding one of these is unbounded for
/// §5.6.</para>
///
/// <para>Stateless, so one instance serves the process (G5).</para>
/// </summary>
public sealed class DiskCleanupHandlers : IDiskCleanupHandlers
{
    public static DiskCleanupHandlers Default { get; } = new();

    private const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    private DiskCleanupHandlers()
    {
    }

    /// <summary>
    /// A registration with a class, and that class registered as an in-process server in this
    /// process's view of the registry. The view is the point: a 32-bit Deguffer on 64-bit Windows sees
    /// the 32-bit registrations, and a class with no 32-bit server cannot be loaded into it.
    /// </summary>
    public bool Serves(string handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);

        if (ClassOf(handler) is not { } classId)
        {
            return false;
        }

        using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId:B}\InprocServer32");

        return server?.GetValue(null) is string { Length: > 0 };
    }

    public DiskCleanupOutcome Run(string handler, string volume, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        // Fails closed on anything that is not the top of a drive in display form. The handler
        // resolves its registered directories against what it is given, so a value that names
        // somewhere else sends Windows' cleanup there.
        if (!Path.IsPathFullyQualified(volume)
            || !string.Equals(Path.GetPathRoot(volume), volume, StringComparison.OrdinalIgnoreCase)
            || volume.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new DiskCleanupOutcome(
                Ran: false,
                $"'{volume}' is not the top of a drive, so Windows was not asked to clear anything on it.");
        }

        if (ClassOf(handler) is not { } classId)
        {
            return new DiskCleanupOutcome(
                Ran: false,
                $"Windows no longer registers its '{handler}' cleanup, so nothing was cleared.");
        }

        // The handler's apartment requirement, met on a thread of our own rather than by initialising
        // whatever thread the caller arrived on, as ShellRecycleBinEmptier does and for the same
        // reason: CoInitialize on a thread-pool thread outlives the call.
        var outcome = new DiskCleanupOutcome(Ran: false, "Windows' cleanup did not run.");

        var thread = new Thread(() => outcome = Perform(handler, classId, volume, ct));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outcome;
    }

    /// <summary>
    /// The class a registration names, from the registration key's default value. Null where the key
    /// is missing or names nothing that parses as a class.
    /// </summary>
    private static Guid? ClassOf(string handler)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}");

        return key?.GetValue(null) is string text && Guid.TryParse(text, out var classId) ? classId : null;
    }

    private static DiskCleanupOutcome Perform(string handler, Guid classId, string volume, CancellationToken ct)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}");

        if (key is null)
        {
            return new DiskCleanupOutcome(
                Ran: false,
                $"Windows no longer registers its '{handler}' cleanup, so nothing was cleared.");
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
            return new DiskCleanupOutcome(
                Ran: false,
                $"Windows would not load its '{handler}' cleanup (0x{created:X8}), so nothing was cleared.");
        }

        try
        {
            return Clean(instance, key, handler, volume, new CancellingCallback(ct));
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static DiskCleanupOutcome Clean(
        object instance,
        RegistryKey key,
        string handler,
        string volume,
        CancellingCallback callback)
    {
        var flags = DiskCleanupNative.UserConsentObtained;
        var handle = key.Handle.DangerousGetHandle();

        IntPtr display, description, button = IntPtr.Zero;

        int initialised;
        IEmptyVolumeCache cache;

        if (instance is IEmptyVolumeCache2 second)
        {
            initialised = second.InitializeEx(handle, volume, handler, out display, out description, out button, ref flags);
            cache = (IEmptyVolumeCache)instance;
        }
        else
        {
            cache = (IEmptyVolumeCache)instance;
            initialised = cache.Initialize(handle, volume, out display, out description, ref flags);
        }

        // Allocated by the handler for a host that shows them, which this one does not.
        Marshal.FreeCoTaskMem(display);
        Marshal.FreeCoTaskMem(description);
        Marshal.FreeCoTaskMem(button);

        // S_FALSE is the handler saying it found nothing to delete here. The plan measured something,
        // so the disk afterwards is what says whether that was true; the executor reads it.
        if (initialised == DiskCleanupNative.S_FALSE)
        {
            return new DiskCleanupOutcome(Ran: true, "Windows found nothing of this to clear.");
        }

        if (initialised < 0)
        {
            return Refused(handler, "would not start", initialised);
        }

        try
        {
            var measured = cache.GetSpaceUsed(out var used, callback);

            if (measured == DiskCleanupNative.E_ABORT)
            {
                return new DiskCleanupOutcome(Ran: false, "Stopped before Windows cleared anything.");
            }

            // S_FALSE here is an error working out the size, not an empty cache. Asked to purge
            // everything, which is what the value Windows documents for "unknown" means.
            var toFree = measured == DiskCleanupNative.S_OK ? used : ulong.MaxValue;
            var purged = cache.Purge(toFree, callback);

            return purged switch
            {
                DiskCleanupNative.S_OK => new DiskCleanupOutcome(Ran: true),
                DiskCleanupNative.E_ABORT => new DiskCleanupOutcome(
                    Ran: false, "Stopped part of the way through, so some of it may still be there."),
                _ => Refused(handler, "stopped", purged),
            };
        }
        finally
        {
            // Always S_OK by its contract, and the flags it returns ask a host to drop the handler from
            // a list this host does not keep.
            cache.Deactivate(out _);
        }
    }

    /// <summary>
    /// A failure with its number, which goes to the user rather than being mapped to a guess: nothing
    /// here can fix what the handler refused, and the number is what tells one refusal from another.
    /// </summary>
    private static DiskCleanupOutcome Refused(string handler, string what, int hresult) =>
        new(Ran: false, $"Windows' '{handler}' cleanup {what} (0x{hresult:X8}).");
}
