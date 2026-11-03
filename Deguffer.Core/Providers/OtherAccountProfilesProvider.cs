using Deguffer.Core.Execution;
using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every other profile on the machine, reported with its size, its last use, whether its account exists
/// and whether it is in use, and the profile of each local account that no longer exists offered for
/// Windows to remove. A machine more than one person used can hold the profiles of deleted accounts,
/// former staff and test accounts, each of which can hold gigabytes.
///
/// <para><b>§5.1: Windows' own removal, <c>DeleteProfileW</c>.</b> Windows keeps an entry for each profile
/// in its list, and a folder deleted without it leaves a broken profile Windows goes on trying to load.
/// Deguffer never deletes a profile's folder by path.</para>
///
/// <para><b>Tier 3, offered one profile at a time.</b> A profile is a person's documents and settings,
/// so it is never lower than Tier 3, and each is an item the user chooses on its own.</para>
///
/// <para><b>Proven gone, or reported and left.</b> <see cref="ProfileJudgement"/> decides, and every
/// profile it does not offer is reported with the reason.</para>
///
/// <para><b>§5.6 by Windows' own list.</b> Afterwards every profile the run did not remove, the running
/// account's included, is still listed with the same folder and that folder is still there, and the
/// removed profile is gone from the list and from the disk (<see cref="ProfileRemovalProof"/>).</para>
/// </summary>
public sealed class OtherAccountProfilesProvider : CleanupProviderBase
{
    /// <summary>
    /// Read by planning and given to the executor that removes, so the plan and the clean ask the same
    /// instance. See <see cref="CleanupProviderBase"/>'s <c>profiles</c>.
    /// </summary>
    private readonly IUserProfiles _profiles;

    /// <summary>The survey presence, the places cleaned and planning all read, taken once per pass (G4).</summary>
    private readonly PassMemo<Task<ProfileSurvey>> _survey = new();

    public OtherAccountProfilesProvider(
        IUserProfiles profiles,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            profiles: profiles)
    {
        _profiles = profiles;
    }

    public override string Id => "other-account-profiles";

    public override string Name => "Other accounts' profiles";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Windows removes each chosen profile: its folder and its entry in Windows' list of profiles. Everything "
        + "the account kept there, its documents, pictures, desktop, downloads, mail and settings, is gone for good, "
        + "and nothing brings it back. Only the profiles of local accounts that no longer exist are offered, so "
        + "nobody can sign in to one of them.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "User profiles",
        Publisher = "Microsoft",
        Purpose = "Windows keeps a profile folder for every account that has signed in to the PC. Deleting an "
            + "account can leave its folder behind, and the folders of former users, test accounts and accounts of "
            + "a domain the PC has left can each hold gigabytes that no other row names.",
        Recommendation = "Removed by Windows' own profile removal, which takes the profile's entry in Windows' "
            + "list with its folder. Only a local account Windows says no longer exists is offered. Every other "
            + "profile is listed with the reason it is not, and a domain or work account's profile is never offered, "
            + "because Windows cannot prove such an account is gone while its domain is out of reach.",
    };

    public override void InvalidateCaches()
    {
        _survey.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Another account's profile, or a list Windows would not give: a refusal is no evidence that there is
    /// none, so the row appears and its plan says what Windows answered.
    /// </summary>
    public override async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var survey = await SurveyAsync(ct).ConfigureAwait(false);

        return survey.Listing.Presence is not PathPresence.Present || survey.Verdicts.Any(IsAnotherAccounts);
    }

    /// <summary>The folder of every profile the survey offers, which Windows removes whole.</summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        var survey = await SurveyAsync(ct).ConfigureAwait(false);

        return
        [
            .. survey.Verdicts
                .Where(verdict => verdict.Standing is ProfileStanding.Offered && verdict.Profile.Folder is not null)
                .Select(verdict => CleanedPlace.Whole(verdict.Profile.Folder!)),
        ];
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var survey = await SurveyAsync(ct).ConfigureAwait(false);

        if (survey.Listing.Presence is not PathPresence.Present)
        {
            return UnexaminedPlan("Windows did not let Deguffer read its list of profiles, so nothing is offered rather than guessed at.");
        }

        var reported = survey.Verdicts.Where(IsAnotherAccounts).ToList();

        if (reported.Count == 0)
        {
            return EmptyPlan("No other account has a profile on this PC.");
        }

        // Every reported profile whose folder is there and is a folder, measured in one call so a walk reads
        // them in one pass. The others have nothing on the disk to measure, or nothing that is theirs.
        var measurable = reported
            .Where(verdict => verdict.Facts is { FolderPresence: PathPresence.Present, FolderIsLink: false })
            .ToList();
        var measured = await MeasureAllAsync([.. measurable.Select(verdict => verdict.Profile.Folder!)], ct).ConfigureAwait(false);
        var sizes = measurable
            .Select((verdict, index) => (verdict.Profile.Sid, Size: measured.Sizes[index], Reached: measured.Reached[index], Stores: measured.MailStores[index]))
            .ToDictionary(entry => entry.Sid, StringComparer.OrdinalIgnoreCase);

        var steps = reported
            .Where(verdict => verdict.Standing is ProfileStanding.Offered)
            .Select(verdict => Step(verdict, sizes[verdict.Profile.Sid], survey))
            .ToList();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            Fallback = measured.Fallback,
            Notes =
            [
                .. measured.Note is { } route ? [route] : Array.Empty<PlanNote>(),
                .. Notes(reported, sizes),
            ],
        };
    }

    /// <summary>
    /// A profile that belongs to an account other than the one Deguffer runs as. Windows' own accounts'
    /// profiles are no one's to report.
    /// </summary>
    private static bool IsAnotherAccounts(ProfileVerdict verdict) =>
        verdict.Standing is not (ProfileStanding.RunningAccount or ProfileStanding.WindowsAccount);

    private RemoveProfileStep Step(
        ProfileVerdict verdict,
        (string Sid, ScanSize Size, bool Reached, IReadOnlyList<string> Stores) measured,
        ProfileSurvey survey)
    {
        var folder = LongPath.Display(verdict.Profile.Folder!);

        return new RemoveProfileStep(
            verdict.Profile,
            $"Remove the profile at {folder}, whose account no longer exists, using Windows' own profile removal")
        {
            Estimated = measured.Size,
            LastWritten = verdict.Profile.LastUsed,
            RequiresElevation = true,
            MailStores = measured.Stores,
            Identity = new ItemIdentity(verdict.Profile.Sid, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
            Facets = [new ItemFacet("Folder", folder)],
            Listed = survey.Verdicts,
            ProfilesFolder = survey.Listing.Folders.Profiles,
        };
    }

    private static IEnumerable<PlanNote> Notes(
        IReadOnlyList<ProfileVerdict> reported,
        IReadOnlyDictionary<string, (string Sid, ScanSize Size, bool Reached, IReadOnlyList<string> Stores)> sizes)
    {
        foreach (var verdict in reported.OrderBy(verdict => verdict.Standing is not ProfileStanding.Offered))
        {
            (ScanSize, bool)? size = sizes.TryGetValue(verdict.Profile.Sid, out var measured) ? (measured.Size, measured.Reached) : null;

            yield return new PlanNote(PlanNoteSeverity.Information, ProfileReport.Line(verdict, size));
        }

        if (sizes.Values.Any(measured => !measured.Reached))
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                "Windows lets only an administrator look inside another account's profile, and only an administrator "
                + "can remove one. Scanning as administrator lets Deguffer measure each profile and offer the ones "
                + "whose account is gone.");
        }
    }

    private Task<ProfileSurvey> SurveyAsync(CancellationToken ct) =>
        _survey.Get(() => Task.Run(() => ProfileSurvey.Take(_profiles, Environment), ct));
}
