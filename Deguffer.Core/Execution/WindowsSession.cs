using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// This machine's Windows session, through the documented Win32 calls for each action.
///
/// <para>Shut down is a full shutdown, as Windows' own <c>shutdown /s</c> is, and not the hybrid
/// one the Start menu makes when Fast Startup is on: the machine comes back from it cold.</para>
///
/// <para>Restart, shut down, sleep and hibernate need <c>SeShutdownPrivilege</c> enabled in the
/// process token. Windows grants it to interactive accounts on a workstation by default, disabled
/// until a program enables it, so it is enabled here for the call that needs it. A policy can take it
/// away, and then the call is refused and the user is told why. Lock and log off need nothing.</para>
///
/// <para>Restart, shut down and log off go through <c>ExitWindowsEx</c> without
/// <c>EWX_FORCE</c>: Windows asks every program to close, and a program with unsaved work may hold the
/// session open to ask its user. Forcing it would throw that work away, which is not a decision a
/// clean is entitled to make.</para>
/// </summary>
public sealed partial class WindowsSession : IWindowsSession
{
    public static readonly WindowsSession Current = new();

    private const uint ExitLogOff = 0x0;
    private const uint ExitReboot = 0x2;
    private const uint ExitPowerOff = 0x8;

    /// <summary><c>SHTDN_REASON_FLAG_PLANNED</c>, with no major or minor reason: the user chose it.</summary>
    private const uint ReasonPlanned = 0x80000000;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    // Read once: whether the machine has a sleep state, and whether hibernation is switched on, do not
    // change while a program runs often enough to be worth asking Windows each time the page is built.
    private readonly Lazy<bool> _canSleep = new(IsPwrSuspendAllowed);
    private readonly Lazy<bool> _canHibernate = new(IsPwrHibernateAllowed);

    private WindowsSession()
    {
    }

    public bool CanSleep => _canSleep.Value;

    public bool CanHibernate => _canHibernate.Value;

    public string? Perform(CompletionAction action) => action switch
    {
        CompletionAction.Lock => Outcome(LockWorkStation()),
        CompletionAction.LogOff => Outcome(ExitWindowsEx(ExitLogOff, ReasonPlanned)),
        CompletionAction.Sleep => WithShutdownPrivilege(() => SetSuspendState(false, false, false)),
        CompletionAction.Hibernate => WithShutdownPrivilege(() => SetSuspendState(true, false, false)),
        CompletionAction.Restart => WithShutdownPrivilege(() => ExitWindowsEx(ExitReboot, ReasonPlanned)),
        CompletionAction.ShutDown => WithShutdownPrivilege(() => ExitWindowsEx(ExitPowerOff, ReasonPlanned)),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not something the Windows session does."),
    };

    /// <summary>Null on success, or Windows' message for the error the call just set.</summary>
    private static string? Outcome(bool succeeded) =>
        succeeded ? null : new Win32Exception(Marshal.GetLastPInvokeError()).Message;

    private static string? WithShutdownPrivilege(Func<bool> call) =>
        EnableShutdownPrivilege() ?? Outcome(call());

    /// <summary>
    /// Enable <c>SeShutdownPrivilege</c> in this process's token. Null on success, or the reason.
    ///
    /// <para><c>AdjustTokenPrivileges</c> reports success for a privilege the token does not hold and
    /// says so only through the last error, so that is read whatever the call returned.</para>
    /// </summary>
    private static string? EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return Outcome(false);
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                return Outcome(false);
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };

            if (!AdjustTokenPrivileges(token, false, privileges, 0, 0, 0))
            {
                return Outcome(false);
            }

            var error = Marshal.GetLastPInvokeError();

            return error == ErrorNotAllAssigned ? new Win32Exception(error).Message : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExitWindowsEx(uint flags, uint reason);

    /// <summary>Its three arguments and its result are each a one-byte <c>BOOLEAN</c>.</summary>
    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeEventsDisabled);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPwrSuspendAllowed();

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPwrHibernateAllowed();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        in TokenPrivileges newState,
        uint previousStateLength,
        nint previousState,
        nint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary><c>TOKEN_PRIVILEGES</c> with room for the one privilege it is ever built with.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }
}
