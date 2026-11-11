namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The Win32 and network error codes the network sources answer with, read as what the reader can
/// do about each. One table for the three native sources, so a share that refuses this account is
/// offered a sign-in whichever of them was asking.
/// </summary>
public static class NetworkErrors
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int BadNetPath = 53;
    public const int NetworkBusy = 54;
    public const int UnexpectedNetworkError = 59;
    public const int NetworkNameDeleted = 64;
    public const int BadNetName = 67;
    public const int InvalidPassword = 86;
    public const int SemaphoreTimeout = 121;
    public const int MoreData = 234;
    public const int NoMoreItems = 259;
    public const int NoNetwork = 1222;
    public const int Cancelled = 1223;
    public const int NetworkUnreachable = 1231;
    public const int HostUnreachable = 1232;
    public const int LogonFailure = 1326;
    public const int AccountRestriction = 1327;
    public const int PasswordExpired = 1330;
    public const int AccountDisabled = 1331;
    public const int NotConnected = 2250;

    /// <summary>What <paramref name="code"/> means to the reader. Never called with <see cref="Success"/>.</summary>
    public static NetworkFailure Of(int code) =>
        code switch
        {
            AccessDenied or InvalidPassword or LogonFailure or AccountRestriction or PasswordExpired or AccountDisabled
                => NetworkFailure.NeedsSignIn,
            FileNotFound or PathNotFound or BadNetPath or BadNetName or NoNetwork or NotConnected
                => NetworkFailure.NotFound,
            SemaphoreTimeout or NetworkBusy or NetworkNameDeleted or UnexpectedNetworkError or NetworkUnreachable or HostUnreachable
                => NetworkFailure.NoAnswer,
            _ => NetworkFailure.Refused,
        };
}
