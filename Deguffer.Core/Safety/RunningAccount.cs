namespace Deguffer.Core.Safety;

/// <summary>Whether the account a process runs as is the one signed in to its Windows session.</summary>
public enum AccountStanding
{
    /// <summary>The same account: the ordinary case, elevated through Admin Approval Mode included.</summary>
    SignedInUser,

    /// <summary>
    /// Another account: the administrator whose credentials a standard user typed in, or the hidden
    /// account Windows' administrator protection elevates to.
    /// </summary>
    AnotherAccount,

    /// <summary>Windows would not say who is signed in, so nobody can say whose account this is.</summary>
    Unconfirmed,
}

/// <summary>
/// The account this process runs as, set against the user signed in to the session, and the folders
/// that account owns.
///
/// <para><b>Why it exists.</b> Every per-account location Deguffer knows (the profile, the application
/// data folders, the temporary folder, the known folders, the Recycle Bin's per-account folder) comes
/// from this process's own token. Elevating with another account's credentials, or under Windows'
/// administrator protection, gives the process a token for another account, with its own profile,
/// folders, registry hive and Recycle Bin. Every row about "your" files would then measure and clear
/// that account's, while saying they are yours. So where the two accounts differ, or nobody can say,
/// Deguffer leaves everything that belongs to one account alone (see
/// <c>Execution.AnotherAccountPlan</c>) and says so on screen (<see cref="Notice"/>). What the whole
/// machine shares is unaffected, and is the reason to elevate.</para>
///
/// <para><b>Withheld rather than redirected to the signed-in user.</b> Redirecting the folders is not
/// enough to make a row the user's. A tool's own eviction command (§5.1) runs as this process's
/// account and reads that account's settings, the Recycle Bin and cloud file calls act on the caller's
/// own, and the environment variables a tool reads are that account's. A row redirected by path alone
/// would be split between two accounts.</para>
/// </summary>
public sealed class RunningAccount
{
    private RunningAccount(
        AccountStanding standing,
        string name,
        string? securityIdentifier,
        SignedInAccount? signedIn,
        IReadOnlyList<string> folders)
    {
        Standing = standing;
        Name = name;
        SecurityIdentifier = securityIdentifier;
        SignedIn = signedIn;
        Folders = folders;
    }

    public AccountStanding Standing { get; }

    public bool IsSignedInUser => Standing == AccountStanding.SignedInUser;

    /// <summary><c>DOMAIN\UserName</c> of the account this process runs as.</summary>
    public string Name { get; }

    /// <summary>The identifier of the account this process runs as, or null where it cannot be established.</summary>
    public string? SecurityIdentifier { get; }

    /// <summary>The account signed in to the session, or null where Windows would not say.</summary>
    public SignedInAccount? SignedIn { get; }

    /// <summary>
    /// Every folder that belongs to the account this process runs as: its profile, its application
    /// data, its temporary folder and each of its known folders, wherever Windows has moved them.
    /// </summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// What the window says while this account is not confirmed as the signed-in user's, or null
    /// while it is.
    /// </summary>
    public string? Notice => Standing switch
    {
        AccountStanding.AnotherAccount =>
            $"Deguffer is running as {Name}, but {SignedIn?.Name} is signed in to Windows. Rows for one "
            + $"account's own files, such as its temporary files, application caches and Recycle Bin, would "
            + $"reach {Name}'s rather than yours, so Deguffer leaves them alone in this window and says so on "
            + "each row. Rows for what the whole machine shares work as usual. The Startup and Installed apps "
            + $"pages list {Name}'s own entries for one account, not yours. To clean your own files, close this "
            + "window and open Deguffer again without administrator rights.",

        AccountStanding.Unconfirmed =>
            $"Deguffer is running as {Name}, and Windows would not say who is signed in. Rows for one "
            + "account's own files, such as its temporary files, application caches and Recycle Bin, might "
            + "reach another account's rather than yours, so Deguffer leaves them alone and says so on each "
            + "row. Rows for what the whole machine shares work as usual. The Startup and Installed apps pages "
            + $"list {Name}'s own entries for one account.",

        _ => null,
    };

    public static RunningAccount Of(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var name = $@"{environment.DomainName}\{environment.UserName}";

        return new RunningAccount(
            StandingOf(environment, name),
            name,
            environment.UserSecurityIdentifier,
            environment.SessionUser,
            FoldersOf(environment));
    }

    /// <summary>
    /// By identifier where both accounts have one, and by name only where the signed-in user's could
    /// not be looked up: a domain account away from its domain has a name and no answer to the lookup.
    /// </summary>
    private static AccountStanding StandingOf(IUserEnvironment environment, string name)
    {
        if (environment.SessionUser is not { } signedIn)
        {
            return AccountStanding.Unconfirmed;
        }

        var same = signedIn.SecurityIdentifier is { } session && environment.UserSecurityIdentifier is { } own
            ? string.Equals(session, own, StringComparison.OrdinalIgnoreCase)
            : string.Equals(signedIn.Name, name, StringComparison.OrdinalIgnoreCase);

        return same ? AccountStanding.SignedInUser : AccountStanding.AnotherAccount;
    }

    private static IReadOnlyList<string> FoldersOf(IUserEnvironment environment) =>
    [
        .. new[]
            {
                environment.UserProfile,
                environment.LocalAppData,
                environment.RoamingAppData,
                environment.LocalLowAppData,
                environment.Videos,
                environment.Documents,
                environment.StartupFolder,
                environment.StartMenuFolder,
                environment.DesktopFolder,
                environment.TempPath,
            }
            .Concat(environment.PersonalFolders)
            .Select(LongPath.Configured)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];
}
