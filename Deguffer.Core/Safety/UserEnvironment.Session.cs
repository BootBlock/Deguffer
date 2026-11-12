using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Deguffer.Core.Safety;

/// <summary>
/// Who is signed in to the Windows session this process runs in, and whose registry hive a setting is
/// read from as a result.
/// </summary>
public sealed partial class UserEnvironment
{
    private const int CurrentSession = -1;
    private const int UserNameClass = 5;
    private const int DomainNameClass = 7;

    // Read once rather than through Invalidate, as the account this process runs as is: a process
    // cannot move to another session's desktop, and nobody signs out of a session a program is
    // running in.
    public SignedInAccount? SessionUser { get; } = ReadSessionUser();

    public IUserHive UserHive => _hives.Value.User;

    public IUserHive? RunningAccountHive => _hives.Value.Running;

    /// <summary>
    /// Asked once, on first use: the choice is <see cref="RunningAccount.Of"/>'s, and it reads the
    /// folders this instance's initialisers set, so it cannot be made in an initialiser of its own.
    /// </summary>
    private (IUserHive User, IUserHive? Running) ChooseHives() => HivesFor(RunningAccount.Of(this));

    /// <summary>
    /// The signed-in user's hive and, where it is another, the running account's, for
    /// <paramref name="account"/>.
    /// </summary>
    internal static (IUserHive User, IUserHive? Running) HivesFor(RunningAccount account) => account.Standing switch
    {
        AccountStanding.SignedInUser => (Safety.UserHive.CurrentUser, null),

        // The user's own settings, from the hive Windows keeps loaded while they are signed in.
        AccountStanding.AnotherAccount when account.SignedIn?.SecurityIdentifier is { } signedIn =>
            (Safety.UserHive.Of(signedIn), Safety.UserHive.CurrentUser),

        // Somebody else's, or nobody can say whose: either way not a setting the user chose.
        _ => (Safety.UserHive.Unread, Safety.UserHive.CurrentUser),
    };

    /// <summary>
    /// The session's user as <c>WTSQuerySessionInformation</c> names it, which is the account that
    /// signed in to the session and not the one a process in it was started as.
    /// </summary>
    private static SignedInAccount? ReadSessionUser()
    {
        if (QuerySession(UserNameClass) is not { Length: > 0 } user || QuerySession(DomainNameClass) is not { } domain)
        {
            return null;
        }

        var name = domain.Length > 0 ? $@"{domain}\{user}" : user;

        // The session's account is almost always this process's own, and then its identifier is the one
        // the token holds. Asking Windows to look a name up can wait on a domain controller, so it is
        // asked only for an account that is not this process's.
        var identifier = string.Equals(name, $@"{Environment.UserDomainName}\{Environment.UserName}", StringComparison.OrdinalIgnoreCase)
            ? WindowsIdentity.GetCurrent().User?.Value
            : IdentifierOf(name);

        return new SignedInAccount(name, identifier);
    }

    private static string? IdentifierOf(string name)
    {
        try
        {
            return new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            // A domain account the machine cannot look up while it is away from the domain. The name
            // is still an answer, and RunningAccount compares by it.
            return null;
        }
        catch (SystemException ex) when (ex.GetType() == typeof(SystemException))
        {
            // Translate reports a lookup that failed for any other reason as a plain SystemException
            // carrying the Win32 error, and the answer is the same: no identifier.
            return null;
        }
    }

    private static string? QuerySession(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
