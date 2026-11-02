using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Deguffer.Core.Profiles;

/// <summary>
/// The Win32 calls behind <see cref="WindowsUserProfiles"/>: the machine's account domain from the Local
/// Security Authority, an account looked up by its identifier, and Windows' own removal of a profile.
/// </summary>
internal static partial class ProfileCalls
{
    private const int ErrorSuccess = 0;

    private const int ErrorAccessDenied = 5;

    private const int ErrorInsufficientBuffer = 122;

    private const int ErrorNoneMapped = 1332;

    /// <summary><c>POLICY_VIEW_LOCAL_INFORMATION</c>, which every account holds.</summary>
    private const uint PolicyViewLocalInformation = 0x1;

    /// <summary><c>PolicyAccountDomainInformation</c> in <c>POLICY_INFORMATION_CLASS</c>.</summary>
    private const int PolicyAccountDomainInformation = 5;

    /// <summary>
    /// The identifier of this machine's own account domain, which every local account's identifier
    /// begins with, or null where the Local Security Authority did not say.
    /// </summary>
    public static unsafe string? MachineAccountDomain()
    {
        var attributes = default(LsaObjectAttributes);

        if (LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyViewLocalInformation, out var policy) != 0)
        {
            return null;
        }

        try
        {
            if (LsaQueryInformationPolicy(policy, PolicyAccountDomainInformation, out var buffer) != 0)
            {
                return null;
            }

            try
            {
                var domain = ((PolicyAccountDomainInfo*)buffer)->DomainSid;

                return domain == IntPtr.Zero ? null : new SecurityIdentifier(domain).Value;
            }
            finally
            {
                LsaFreeMemory(buffer);
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    /// <summary>
    /// Whether an account with <paramref name="sid"/> exists, asked of the Local Security Authority. Only
    /// <c>ERROR_NONE_MAPPED</c> is read as "no such account": every other failure is an unanswered
    /// question, and a domain that cannot be reached is one of them.
    /// </summary>
    public static AccountReading LookUp(string sid)
    {
        byte[] binary;

        try
        {
            var identifier = new SecurityIdentifier(sid);
            binary = new byte[identifier.BinaryLength];
            identifier.GetBinaryForm(binary, 0);
        }
        catch (ArgumentException)
        {
            return new AccountReading(AccountAnswer.Unanswered, Error: 87);
        }

        var nameLength = 256u;
        var domainLength = 256u;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var name = new char[nameLength];
            var domain = new char[domainLength];

            if (LookupAccountSid(null, binary, name, ref nameLength, domain, ref domainLength, out _))
            {
                return new AccountReading(AccountAnswer.Exists);
            }

            var error = Marshal.GetLastPInvokeError();

            if (error != ErrorInsufficientBuffer)
            {
                return error == ErrorNoneMapped
                    ? new AccountReading(AccountAnswer.NotFound)
                    : new AccountReading(AccountAnswer.Unanswered, error);
            }
        }

        // Windows asked for more room twice. That takes an account to name, but an answer that was never
        // given is not read as one.
        return new AccountReading(AccountAnswer.Unanswered, ErrorInsufficientBuffer);
    }

    /// <summary>
    /// Windows' own removal of the profile of <paramref name="sid"/>. No path and no computer is given,
    /// so Windows removes the folder its own list records, on this machine.
    /// </summary>
    public static ProfileRemoval Remove(string sid)
    {
        if (DeleteProfile(sid, null, null))
        {
            return new ProfileRemoval(ProfileRemovalAnswer.Removed);
        }

        return Marshal.GetLastPInvokeError() switch
        {
            ErrorSuccess => new ProfileRemoval(ProfileRemovalAnswer.Failed),
            ErrorAccessDenied => new ProfileRemoval(ProfileRemovalAnswer.NeedsElevation, ErrorAccessDenied),
            var error => new ProfileRemoval(ProfileRemovalAnswer.Failed, error),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PolicyAccountDomainInfo
    {
        public LsaUnicodeString DomainName;
        public IntPtr DomainSid;
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaQueryInformationPolicy(IntPtr policy, int informationClass, out IntPtr buffer);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaFreeMemory(IntPtr buffer);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaClose(IntPtr policy);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountSid(
        string? systemName,
        byte[] sid,
        [Out] char[] name,
        ref uint nameLength,
        [Out] char[] domain,
        ref uint domainLength,
        out int use);

    [LibraryImport("userenv.dll", EntryPoint = "DeleteProfileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteProfile(string sid, string? profilePath, string? computerName);
}
