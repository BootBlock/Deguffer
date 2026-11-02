using Deguffer.Core.Profiles;

namespace Deguffer.Core.Execution;

/// <summary>
/// Ask Windows to remove one profile whose account no longer exists, with its own
/// <c>DeleteProfileW</c> (§5.1). Windows takes the folder it records and the profile's entry in its list
/// together, and Deguffer never deletes the folder by path: a folder deleted alone leaves an entry that
/// Windows goes on trying to load.
///
/// <para><b>Decided twice.</b> The plan judged the profile, and the run judges it again on the same rules
/// immediately before the removal (<see cref="ProfileRemovalRun"/>), because an account can be made again
/// and a profile loaded while the preview is on screen.</para>
///
/// <para><b>Its §5.6 negative is every other profile.</b> The step carries Windows' whole list as it
/// stood when the plan was made, and <see cref="ProfileRemovalProof"/> asks Windows again afterwards
/// that each profile this run did not remove is still listed, with the same folder, and that folder
/// still there.</para>
/// </summary>
/// <param name="Profile">The profile to remove, as Windows listed it when the plan was made.</param>
/// <param name="What">What the user is told this step will do.</param>
public sealed record RemoveProfileStep(UserProfile Profile, string What) : CleanupStep
{
    /// <summary>
    /// Every profile Windows listed when the plan was made, this one included, with what was decided
    /// about each. The same list on every step of one plan.
    /// </summary>
    public IReadOnlyList<ProfileVerdict> Listed { get; init; } = [];

    /// <summary>The folder Windows keeps profiles in when the plan was made, which the run must leave.</summary>
    public string? ProfilesFolder { get; init; }

    /// <summary>The folder Windows records for the profile, which every offered profile has.</summary>
    public string Folder => Profile.Folder
        ?? throw new InvalidOperationException("A profile is offered only where Windows records a folder for it.");

    public override string Description => What;

    /// <summary>The account's identifier, which no other profile on the machine shares.</summary>
    public override string SelectionKey => Profile.Sid;

    /// <summary>
    /// None. A profile the user declines is proved standing with every other profile, by Windows' own
    /// list rather than by its path (see <see cref="ProfileRemovalProof"/>).
    /// </summary>
    public override IReadOnlyList<string> Subjects => [];
}
