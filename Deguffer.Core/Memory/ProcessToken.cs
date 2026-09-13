using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads the account and the integrity level out of one process's token, which is where §7.2.1 takes
/// both from: the token's user answers whose process it is, and its mandatory label answers whether
/// Deguffer may post to its windows at all.
///
/// <para>The process handle needs only <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which
/// <c>OpenProcessToken</c> documents, and the token is opened for <c>TOKEN_QUERY</c> alone. Either
/// step failing reads as a fact that could not be read rather than as an answer.</para>
/// </summary>
internal static partial class ProcessToken
{
    private const uint TokenQuery = 0x0008;

    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;

    public static TokenFacts Read(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return default;
        }

        try
        {
            return new TokenFacts(User(token), Integrity(token));
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// <c>TOKEN_USER</c> opens with a <c>SID_AND_ATTRIBUTES</c>, whose first member points at the
    /// security identifier inside the same buffer.
    /// </summary>
    private static string? User(nint token)
    {
        var buffer = Information(token, TokenUser);

        if (buffer == 0)
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The integrity level is the last subauthority of the label's security identifier, which is how
    /// Microsoft's own sample reads it. <see cref="IntegrityLevel"/> holds what the value means.
    /// </summary>
    private static int? Integrity(nint token)
    {
        var buffer = Information(token, TokenIntegrityLevel);

        if (buffer == 0)
        {
            return null;
        }

        try
        {
            var label = Marshal.ReadIntPtr(buffer);
            var subauthorities = Marshal.ReadByte(GetSidSubAuthorityCount(label));

            return subauthorities == 0
                ? null
                : Marshal.ReadInt32(GetSidSubAuthority(label, (uint)(subauthorities - 1)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// One token information class, in unmanaged memory the caller frees, or zero where Windows would
    /// not answer. It is unmanaged memory rather than an array because the structures Windows returns
    /// point into their own buffer, and a pointer into a managed array is worth nothing once the call
    /// has returned.
    /// </summary>
    private static nint Information(nint token, int informationClass)
    {
        // The documented protocol: the first call fails and reports the length it needed.
        GetTokenInformation(token, informationClass, 0, 0, out var needed);

        if (needed <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(needed);

        if (GetTokenInformation(token, informationClass, buffer, needed, out _))
        {
            return buffer;
        }

        Marshal.FreeHGlobal(buffer);
        return 0;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int needed);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthority(nint sid, uint index);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
