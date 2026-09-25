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
/// <em>Windows ESD installation files</em> and three more, and it implements only the first handler
/// interface, so it is never told the key's name at all. What it reads to tell which it is being are
/// the values on the key: <c>SetupPrevInst</c> and <c>RemoveUninstall</c> on one,
/// <c>SetupDirectories</c> on another. A host that passed any other key would get a different cleanup
/// from the same class.</para>
///
/// <para><b>What was observed of <c>setupcln.dll</c>, on Windows 11 24H2.</b> It answers "nothing to
/// delete" for every role to a process that is not elevated. Elevated, it answers for a volume given
/// as <c>C:\</c> and not for <c>C:</c>, which is why the volume crosses with its separator. Nothing on
/// the handler's side is documented beyond the interface, which is why the plan measures every
/// directory the registration names before and after, and why a run holding one of these is
/// unbounded for §5.6.</para>
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

    public DiskCleanupSurvey Survey(string handler, string volume, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        if (NotADrive(volume) is { } refused)
        {
            return new DiskCleanupSurvey(DiskCleanupAnswer.Unavailable, refused);
        }

        if (ClassOf(handler) is not { } classId)
        {
            return new DiskCleanupSurvey(DiskCleanupAnswer.Unavailable, Unregistered(handler));
        }

        // Asked after the registration, so a handler that is not there is reported as missing
        // rather than as something elevating would reveal.
        if (!Environment.IsPrivilegedProcess)
        {
            return new DiskCleanupSurvey(DiskCleanupAnswer.NeedsElevation);
        }

        return OnItsOwnThread(() => Hosted(
            handler,
            classId,
            volume,
            new CancellingCallback(ct),
            why => new DiskCleanupSurvey(DiskCleanupAnswer.Unavailable, why),
            (cache, callback) =>
            {
                var measured = cache.GetSpaceUsed(out var used, callback);

                // S_FALSE from here is an error working out the size rather than an empty cache, and
                // the handler has already said it has something by starting, so it is offered.
                return measured == DiskCleanupNative.S_OK && used == 0
                    ? new DiskCleanupSurvey(DiskCleanupAnswer.NothingToClear)
                    : new DiskCleanupSurvey(DiskCleanupAnswer.HasSomething);
            },
            () => new DiskCleanupSurvey(DiskCleanupAnswer.NothingToClear)));
    }

    public DiskCleanupOutcome Run(string handler, string volume, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        if (NotADrive(volume) is { } refused)
        {
            return new DiskCleanupOutcome(Ran: false, refused);
        }

        if (ClassOf(handler) is not { } classId)
        {
            return new DiskCleanupOutcome(Ran: false, Unregistered(handler));
        }

        return OnItsOwnThread(() => Hosted(
            handler,
            classId,
            volume,
            new CancellingCallback(ct),
            why => new DiskCleanupOutcome(Ran: false, why),
            (cache, callback) =>
            {
                var measured = cache.GetSpaceUsed(out var used, callback);

                if (measured == DiskCleanupNative.E_ABORT)
                {
                    return new DiskCleanupOutcome(Ran: false, "Stopped before Windows cleared anything.");
                }

                // Asked to purge everything where the size could not be worked out, which is what
                // the value Windows documents for "unknown" means.
                var purged = cache.Purge(measured == DiskCleanupNative.S_OK ? used : ulong.MaxValue, callback);

                return purged switch
                {
                    DiskCleanupNative.S_OK => new DiskCleanupOutcome(Ran: true),
                    DiskCleanupNative.E_ABORT => new DiskCleanupOutcome(
                        Ran: false, "Stopped part of the way through, so some of it may still be there."),
                    _ => new DiskCleanupOutcome(Ran: false, Refused(handler, "stopped", purged)),
                };
            },
            // The plan measured something, so the disk afterwards is what says whether this was true.
            () => new DiskCleanupOutcome(Ran: true, "Windows found nothing of this to clear.")));
    }

    /// <summary>
    /// Why <paramref name="volume"/> is not something a handler may be given, or null where it is the
    /// top of a drive in display form. The handler resolves its registered directories against what
    /// it is given, so a value that names somewhere else sends Windows' cleanup there.
    /// </summary>
    private static string? NotADrive(string volume) =>
        Path.IsPathFullyQualified(volume)
        && string.Equals(Path.GetPathRoot(volume), volume, StringComparison.OrdinalIgnoreCase)
        && !volume.StartsWith(@"\\", StringComparison.Ordinal)
            ? null
            : $"'{volume}' is not the top of a drive, so Windows was not asked to clear anything on it.";

    private static string Unregistered(string handler) =>
        $"Windows no longer registers its '{handler}' cleanup, so nothing was cleared.";

    /// <summary>
    /// A failure with its number, which goes to the user rather than being mapped to a guess: nothing
    /// here can fix what the handler refused, and the number is what tells one refusal from another.
    /// </summary>
    private static string Refused(string handler, string what, int hresult) =>
        $"Windows' '{handler}' cleanup {what} (0x{hresult:X8}).";

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

    /// <summary>
    /// The handler's apartment requirement, met on a thread of our own rather than by initialising
    /// whatever thread the caller arrived on, as <see cref="ShellRecycleBinEmptier"/> does and for the
    /// same reason: CoInitialize on a thread-pool thread outlives the call.
    /// </summary>
    private static T OnItsOwnThread<T>(Func<T> work)
    {
        T result = default!;

        var thread = new Thread(() => result = work());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }

    /// <summary>
    /// One handler created, initialised with its own registration's key, handed to
    /// <paramref name="started"/>, and deactivated and released whatever happens.
    /// </summary>
    /// <param name="failed">What to answer where it would not load or start, given why, for the user.</param>
    /// <param name="started">What to do with a handler that started and has something to clear.</param>
    /// <param name="nothing">What to answer where it started and found nothing of its own.</param>
    private static T Hosted<T>(
        string handler,
        Guid classId,
        string volume,
        CancellingCallback callback,
        Func<string, T> failed,
        Func<IEmptyVolumeCache, CancellingCallback, T> started,
        Func<T> nothing)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}");

        if (key is null)
        {
            return failed(Unregistered(handler));
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
            return failed(Refused(handler, "would not load", created));
        }

        try
        {
            var flags = DiskCleanupNative.UserConsentObtained;
            var handle = key.Handle.DangerousGetHandle();
            var cache = (IEmptyVolumeCache)instance;

            IntPtr display, description, button = IntPtr.Zero;

            var initialised = instance is IEmptyVolumeCache2 second
                ? second.InitializeEx(handle, volume, handler, out display, out description, out button, ref flags)
                : cache.Initialize(handle, volume, out display, out description, ref flags);

            // Allocated by the handler for a host that shows them, which this one does not.
            Marshal.FreeCoTaskMem(display);
            Marshal.FreeCoTaskMem(description);
            Marshal.FreeCoTaskMem(button);

            if (initialised == DiskCleanupNative.S_FALSE)
            {
                return nothing();
            }

            if (initialised < 0)
            {
                return failed(Refused(handler, "would not start", initialised));
            }

            try
            {
                return started(cache, callback);
            }
            finally
            {
                // Always S_OK by its contract, and the flags it returns ask a host to drop the handler
                // from a list this host does not keep.
                cache.Deactivate(out _);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
