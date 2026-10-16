using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Testing;

/// <summary>
/// The calling thread, for the length of a <c>using</c> block, acting on a copy of this process's
/// token that does not hold <c>SeDebugPrivilege</c>.
///
/// <para><c>OpenProcess</c> grants whatever is asked of a caller with that privilege enabled,
/// whatever the target's security descriptor says, so no fixture can make a process refuse such a
/// caller. An elevated account holds it disabled, but <see cref="System.Diagnostics.Process"/>
/// enables it in the process token the first time it looks a process up by id or lists them, as
/// <c>ProcessInspector</c> itself does. Measured, not assumed: an elevated test host had it enabled
/// by the time a test ran, and the empty-DACL ping the suite builds opened for it. A test that needs
/// a refusal it built itself therefore asks from a token without the privilege, rather than relying
/// on the account that runs the suite.</para>
///
/// <para>The privilege is removed from a copy, never disabled in the process token, and only this
/// thread wears the copy: the suite runs tests in parallel, and every other thread keeps the token it
/// had. Removed rather than disabled, so nothing the code under test calls can enable it again.</para>
///
/// <para>The code inside the block must run on this thread, so it must not await.</para>
/// </summary>
public sealed class ThreadWithoutDebugPrivilege : IDisposable
{
    private const string DebugPrivilege = "SeDebugPrivilege";

    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;

    private const int SecurityImpersonation = 2;
    private const int TokenTypeImpersonation = 2;
    private const int TokenPrivilegesClass = 3;

    private const uint PrivilegeRemoved = 0x4;
    private const int ErrorNotAllAssigned = 1300;

    public ThreadWithoutDebugPrivilege()
    {
        var copy = CopyWithoutDebugPrivilege();

        try
        {
            // Read back rather than trusted to the adjustment: a refusal asked for under a token
            // still holding the privilege would prove nothing, and the test would not say so.
            Assert.False(HoldsDebugPrivilege(copy), $"The copied token still holds {DebugPrivilege}.");

            if (!SetThreadToken(0, copy))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // The thread keeps its own reference to the token it was given.
            CloseHandle(copy);
        }
    }

    public void Dispose()
    {
        if (!RevertToSelf())
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static nint CopyWithoutDebugPrivilege()
    {
        var privileges = new TokenPrivileges { Count = 1, Luid = DebugPrivilegeLuid(), Attributes = PrivilegeRemoved };

        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate, out var process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        nint copy;

        try
        {
            if (!DuplicateTokenEx(
                    process,
                    TokenQuery | TokenImpersonate | TokenAdjustPrivileges,
                    0,
                    SecurityImpersonation,
                    TokenTypeImpersonation,
                    out copy))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            CloseHandle(process);
        }

        // A token that never held the privilege answers success with ERROR_NOT_ALL_ASSIGNED, and is
        // already what is wanted.
        var adjusted = AdjustTokenPrivileges(copy, false, privileges, 0, 0, 0);
        var error = Marshal.GetLastPInvokeError();

        if (adjusted && error is 0 or ErrorNotAllAssigned)
        {
            return copy;
        }

        CloseHandle(copy);
        throw new Win32Exception(error);
    }

    /// <summary>
    /// Whether <paramref name="token"/> lists the privilege at all, enabled or not.
    /// <c>TOKEN_PRIVILEGES</c> is a count followed by that many <c>LUID_AND_ATTRIBUTES</c>.
    /// </summary>
    private static bool HoldsDebugPrivilege(nint token)
    {
        GetTokenInformation(token, TokenPrivilegesClass, 0, 0, out var needed);
        var buffer = Marshal.AllocHGlobal(needed);

        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var wanted = DebugPrivilegeLuid();
            var count = Marshal.ReadInt32(buffer);
            var size = Marshal.SizeOf<LuidAndAttributes>();

            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStructure<LuidAndAttributes>(buffer + sizeof(uint) + (i * size)).Luid == wanted)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Luid DebugPrivilegeLuid() =>
        LookupPrivilegeValue(null, DebugPrivilege, out var luid)
            ? luid
            : throw new Win32Exception(Marshal.GetLastPInvokeError());

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        nint token,
        uint access,
        nint attributes,
        int impersonationLevel,
        int tokenType,
        out nint copy);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        in TokenPrivileges newState,
        uint previousStateLength,
        nint previousState,
        nint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadToken(nint thread, nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [StructLayout(LayoutKind.Sequential)]
    private record struct Luid(uint LowPart, int HighPart);

    [StructLayout(LayoutKind.Sequential)]
    private record struct LuidAndAttributes(Luid Luid, uint Attributes);

    /// <summary><c>TOKEN_PRIVILEGES</c> with room for the one privilege it is ever built with.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }
}
