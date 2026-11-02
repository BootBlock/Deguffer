using Deguffer.Core.Execution;
using Deguffer.Core.Profiles;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Other accounts' profiles: every one reported, and only a local account Windows says no longer exists
/// offered, for Windows' own <c>DeleteProfileW</c> to remove at Tier 3, proved afterwards by Windows' own
/// list.
///
/// <para>The profiles are <see cref="FakeUserProfiles"/> throughout, with real folders under a temporary
/// directory. The real one removes the profiles of the machine that runs the suite.</para>
/// </summary>
public sealed class OtherAccountProfilesProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeUserProfiles _profiles;

    public OtherAccountProfilesProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _profiles = new FakeUserProfiles(Path.Combine(_temp.Path, "Users"));

        // The account Deguffer runs as, whose profile the fake environment keeps outside the profiles
        // folder. It is loaded, as a signed-in account's profile is.
        _profiles.AddEntry(new UserProfile(_environment.UserSecurityIdentifier!, _environment.UserProfile, ProfileLoad.Loaded, LastUsed: null));
        _profiles.Accounts[_environment.UserSecurityIdentifier!] = new AccountReading(AccountAnswer.Exists);
    }

    public void Dispose() => _temp.Dispose();

    private OtherAccountProfilesProvider Provider() =>
        new(_profiles, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private static RemoveProfileStep Step(CleanupPlan plan) => Assert.IsType<RemoveProfileStep>(Assert.Single(plan.Steps));

    private static string NoteAbout(CleanupPlan plan, UserProfile profile) =>
        Assert.Single(plan.Notes, note => note.Message.StartsWith(ProfileReport.Name(profile), StringComparison.Ordinal)).Message;

    [Fact]
    public async Task ALocalAccountThatNoLongerExistsIsOfferedForWindowsToRemove()
    {
        var gone = _profiles.AddLocal(1002, "former", bytes: 5000);

        var plan = await Provider().PlanAsync();
        var step = Step(plan);

        Assert.Equal(gone.Sid, step.Profile.Sid);
        Assert.Equal(gone.Sid, step.Identity!.Key);
        Assert.Equal("former", step.Identity.Name);
        Assert.Equal(5000, step.EstimatedBytes);
        Assert.True(step.RequiresElevation);
        Assert.Equal(gone.LastUsed, step.LastWritten);

        // Windows' removal, never a removal by path.
        Assert.IsNotAssignableFrom<DeleteStep>(step);
        Assert.Empty(plan.TargetedPaths);
        Assert.Contains("Windows found no account for it", NoteAbout(plan, gone), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItIsTier3NeverPreSelectedAndSaysNothingBringsItBack()
    {
        _profiles.AddLocal(1002, "former");

        var provider = Provider();
        var requirement = ConfirmationRequirement.For(await provider.PlanAsync());

        Assert.Equal(SafetyTier.UserData, provider.Tier);
        Assert.False(provider.Tier.IsPreSelectedByDefault());
        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Contains("nothing brings it back", requirement.Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountThatStillExistsIsReportedAndNotOffered()
    {
        var alive = _profiles.AddLocal(1003, "colleague");
        _profiles.Accounts[alive.Sid] = new AccountReading(AccountAnswer.Exists);

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("its account exists", NoteAbout(plan, alive), StringComparison.Ordinal);
        Assert.Contains("Not offered, because its account still exists", NoteAbout(plan, alive), StringComparison.Ordinal);
    }

    /// <summary>
    /// A domain account fails to resolve away from its domain controller exactly as a deleted one does,
    /// so a domain identifier Windows cannot find is reported, never offered.
    /// </summary>
    [Fact]
    public async Task ADomainAccountThatDoesNotResolveIsReportedButNeverOffered()
    {
        var domain = _profiles.Add($"{FakeUserProfiles.OtherDomain}-1104", "jsmith");

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("it belongs to a domain account", NoteAbout(plan, domain), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountLookupWindowsDidNotAnswerIsNeverReadAsAbsence()
    {
        var unanswered = _profiles.AddLocal(1005, "unknown");
        _profiles.Accounts[unanswered.Sid] = new AccountReading(AccountAnswer.Unanswered, Error: 1789);

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("(error 1789)", NoteAbout(plan, unanswered), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheMachinesAccountDomainNoAccountIsProvedLocal()
    {
        var gone = _profiles.AddLocal(1002, "former");
        _profiles.Domain = null;

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("no account could be proved gone", NoteAbout(plan, gone), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProfileLoad.Loaded, "Windows has it loaded")]
    [InlineData(ProfileLoad.Unknown, "Windows did not say whether something is using it")]
    public async Task AProfileThatMayBeInUseIsNotOffered(ProfileLoad load, string why)
    {
        var loaded = _profiles.AddLocal(1002, "former", load: load);

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(why, NoteAbout(plan, loaded), StringComparison.Ordinal);
    }

    /// <summary>
    /// An elevated Deguffer runs as the administrator who approved it, so its own profile is never offered,
    /// and nor is a folder that holds it, whatever identifier names it.
    /// </summary>
    [Fact]
    public async Task TheRunningAccountsProfileIsNeitherOfferedNorReportedEvenUnderAnotherIdentifier()
    {
        var running = Assert.Single(_profiles.Profiles);
        _profiles.Profiles.Clear();
        _profiles.AddEntry(running with { Load = ProfileLoad.NotLoaded, Sid = $"{FakeUserProfiles.MachineDomain}-1001" });
        var holder = _profiles.AddEntry(new UserProfile($"{FakeUserProfiles.MachineDomain}-1009", _temp.Path, ProfileLoad.NotLoaded, null));

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.DoesNotContain(plan.Notes, note => note.Message.StartsWith(ProfileReport.Name(running), StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, note => note.Message.StartsWith(ProfileReport.Name(holder), StringComparison.Ordinal));
    }

    /// <summary>
    /// Windows' own accounts' profiles are loaded whenever Windows runs, so they must be recognised as
    /// Windows' before they are judged as in use, or every machine would report them.
    /// </summary>
    [Theory]
    [InlineData(ProfileLoad.Loaded)]
    [InlineData(ProfileLoad.NotLoaded)]
    public async Task WindowsOwnAccountsAreNeitherOfferedNorReported(ProfileLoad load)
    {
        _profiles.AddEntry(new UserProfile("S-1-5-18", Path.Combine(_temp.Path, "systemprofile"), load, null));
        _profiles.AddEntry(new UserProfile(
            "S-1-5-80-3238277391-1891473654-1195688043-4149050645-2494734967", Path.Combine(_temp.Path, "service"), load, null));

        var provider = Provider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task AnEntryNotNamedAfterAnAccountIsReportedAndNotOffered()
    {
        var copy = _profiles.Add($"{FakeUserProfiles.MachineDomain}-1002.bak", "former");

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("not named after an account", NoteAbout(plan, copy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntraAccountIsReportedAndNeverOffered()
    {
        var cloud = _profiles.Add("S-1-12-1-1111111111-2222222222-3333333333-4444444444", "AzureAD-user");

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("Microsoft Entra", NoteAbout(plan, cloud), StringComparison.Ordinal);
    }

    /// <summary>§5.2: Windows removes whatever folder it records, so only a recognised one is offered.</summary>
    [Fact]
    public async Task AFolderOutsideTheProfilesFolderIsNotOffered()
    {
        var elsewhere = Path.Combine(_temp.Path, "Elsewhere", "former");
        Directory.CreateDirectory(elsewhere);
        var outside = _profiles.AddEntry(new UserProfile($"{FakeUserProfiles.MachineDomain}-1002", elsewhere, ProfileLoad.NotLoaded, null));
        var shipped = _profiles.AddEntry(new UserProfile(
            $"{FakeUserProfiles.MachineDomain}-1003", Path.Combine(_profiles.ProfilesFolder, "Default"), ProfileLoad.NotLoaded, null));
        Directory.CreateDirectory(shipped.Folder!);

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("not one Windows keeps profiles in", NoteAbout(plan, outside), StringComparison.Ordinal);
        Assert.Contains("not one Windows keeps profiles in", NoteAbout(plan, shipped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderTwoEntriesNameIsNotOfferedForEither()
    {
        var first = _profiles.AddLocal(1002, "former");
        var second = _profiles.AddEntry(first with { Sid = $"{FakeUserProfiles.MachineDomain}-1003" });

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.All(
            plan.Notes.Where(note => note.Message.StartsWith(ProfileReport.Name(first), StringComparison.Ordinal)),
            note => Assert.Contains("names the same folder", note.Message, StringComparison.Ordinal));
        Assert.Equal(2, plan.Notes.Count(note => note.Message.Contains("names the same folder", StringComparison.Ordinal)));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task AFolderThatIsALinkIsNotOffered()
    {
        var target = Path.Combine(_temp.Path, "Target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_profiles.ProfilesFolder, "former");
        Junction.ToDirectory(link, target);
        var linked = _profiles.AddEntry(new UserProfile($"{FakeUserProfiles.MachineDomain}-1002", link, ProfileLoad.NotLoaded, null));

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("a link to somewhere else", NoteAbout(plan, linked), StringComparison.Ordinal);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task AnEntryWhoseFolderIsGoneIsReportedAndNotOffered()
    {
        var bare = _profiles.AddEntry(new UserProfile(
            $"{FakeUserProfiles.MachineDomain}-1002", Path.Combine(_profiles.ProfilesFolder, "former"), ProfileLoad.NotLoaded, null));

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains("its folder is already gone", NoteAbout(plan, bare), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListWindowsRefusedIsNotExaminedAndTheRowAppears()
    {
        _profiles.ListPresence = PathPresence.Refused;

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.True(plan.WasNotExamined);
        Assert.Empty(plan.Steps);
    }

    /// <summary>§9: Windows removes a profile whole, so one holding an Outlook data file is withheld and the file protected.</summary>
    [Fact]
    public async Task AProfileHoldingAnOutlookDataFileIsWithheld()
    {
        var gone = _profiles.AddLocal(1002, "former");
        var store = Path.Combine(gone.Folder!, "Documents", "Outlook Files", "archive.pst");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllBytes(store, new byte[100]);

        var plan = await Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.StartsWith($"Leaving the profile at {gone.Folder}", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, path => string.Equals(path.Path, store, StringComparison.OrdinalIgnoreCase));
    }
}
