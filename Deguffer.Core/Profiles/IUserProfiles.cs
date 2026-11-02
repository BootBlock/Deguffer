namespace Deguffer.Core.Profiles;

/// <summary>How Windows answered whether an account exists.</summary>
public enum AccountAnswer
{
    /// <summary>Windows named the account.</summary>
    Exists,

    /// <summary>Windows answered that no account has the identifier (<c>ERROR_NONE_MAPPED</c>).</summary>
    NotFound,

    /// <summary>Windows did not answer, for the reason <see cref="AccountReading.Error"/> gives.</summary>
    Unanswered,
}

/// <summary>What Windows answered about one account.</summary>
/// <param name="Error">The Win32 error Windows gave where it did not answer, and 0 otherwise.</param>
public sealed record AccountReading(AccountAnswer Answer, int Error = 0);

/// <summary>How Windows answered a request to remove one profile.</summary>
public enum ProfileRemovalAnswer
{
    Removed,

    /// <summary>Windows removes a profile only for an administrator.</summary>
    NeedsElevation,

    /// <summary>Windows did not remove it, for the reason <see cref="ProfileRemoval.Error"/> gives.</summary>
    Failed,
}

/// <summary>What Windows answered to a request to remove one profile.</summary>
/// <param name="Error">The Win32 error Windows gave where it did not remove the profile, and 0 otherwise.</param>
public sealed record ProfileRemoval(ProfileRemovalAnswer Answer, int Error = 0);

/// <summary>
/// Windows' profiles: the list it keeps, the accounts they belong to, and its own removal of one.
///
/// <para>Behind an interface because every member reaches the machine, and one of them removes a whole
/// profile. A test that reached the real one would plan against, and could remove, the profiles of the
/// machine that ran the suite. <see cref="WindowsUserProfiles"/> is the machine's.</para>
///
/// <para>Every member blocks, and none throws for a refusal: each answers with what Windows said.</para>
/// </summary>
public interface IUserProfiles
{
    /// <summary>Every entry in Windows' list of profiles, read at the moment of the call.</summary>
    ProfileListing List();

    /// <summary>
    /// The security identifier of this machine's own account domain, the one its local accounts belong
    /// to, or null where Windows did not say.
    /// </summary>
    string? MachineAccountDomain();

    /// <summary>Whether an account with the identifier <paramref name="sid"/> exists.</summary>
    AccountReading LookUp(string sid);

    /// <summary>
    /// Ask Windows to remove the profile of <paramref name="sid"/> with its own <c>DeleteProfileW</c>,
    /// which takes the folder Windows records for it and its entry in the list together. Deguffer never
    /// names the folder: Windows removes the one it records.
    /// </summary>
    ProfileRemoval Remove(string sid);
}
