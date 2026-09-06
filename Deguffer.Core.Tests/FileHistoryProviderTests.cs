using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The File History target: Tier 3, cleared by Windows' own command, and never by a path deletion.
///
/// <para>Two things separate this from every other command provider, and most of what follows is
/// about one or the other. The command takes an <em>age</em>, so the figure Deguffer shows is the
/// aged part of the folder rather than the whole of it — which makes a zero mean "nothing old
/// enough" rather than "already clear". And the target is shared twice over, by account and then by
/// machine, so the folders that must survive are siblings of the one being measured.</para>
///
/// <para>Everything runs against a synthetic profile and a synthetic backup drive. A test that only
/// passed on a machine with File History switched on would be asserting the machine.</para>
/// </summary>
public sealed class FileHistoryProviderTests : IDisposable
{
    /// <summary>Recognisably invented, and shaped like a second person's account and a second machine.</summary>
    private const string AnotherAccount = "otheruser";

    private const string AnotherMachine = "OTHERMACHINE";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeProcessRunner _runner = new();

    public FileHistoryProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("FhManagew");
    }

    public void Dispose() => _temp.Dispose();

    private FileHistoryProvider CreateProvider(AppPreferences? preferences = null) => new(
        _environment,
        _runner,
        FakeProcessInspector.NothingRunning,
        new FakeDirectoryScanner(),
        new FakePreferences(preferences ?? AppPreferences.Default));

    /// <summary>A drive root under the scratch tree, standing in for a backup disk.</summary>
    private string CreateDrive(string name = "E") => _temp.CreateDirectory("drives", name);

    /// <summary>This machine's history folder on <paramref name="drive"/>, as a path.</summary>
    private static string CreateHistory(string drive) => Path.Combine(
        drive, "FileHistory", FakeUserEnvironment.Account, FakeUserEnvironment.Machine);

    /// <summary>
    /// Somebody else's whole File History on the same drive, created and holding a saved version.
    /// This is the sibling §5.2 is about: identical in shape to this user's, and not theirs to trim.
    /// </summary>
    private static string CreateAnotherAccountsHistory(string drive)
    {
        var theirs = Path.Combine(drive, "FileHistory", AnotherAccount);

        Directory.CreateDirectory(Path.Combine(theirs, AnotherMachine, "Data"));
        File.WriteAllBytes(Path.Combine(theirs, AnotherMachine, "Data", "theirs.docx"), new byte[2048]);

        return theirs;
    }

    /// <summary>This user's history of a different machine, created. Their other laptop's backup.</summary>
    private static string CreateAnotherMachinesHistory(string drive) => Directory.CreateDirectory(
        Path.Combine(drive, "FileHistory", FakeUserEnvironment.Account, AnotherMachine)).FullName;

    /// <summary>This machine's <c>Data</c> folder, created.</summary>
    private string CreateData(string drive)
    {
        var data = Path.Combine(CreateHistory(drive), "Data");
        Directory.CreateDirectory(data);
        return data;
    }

    /// <summary>
    /// One saved version, aged. Both timestamps move, because <see cref="MinimumAge"/> reads the
    /// newer of the two — see <see cref="TempDirectory.Age"/>.
    /// </summary>
    private static string CreateVersion(string data, string name, int bytes, TimeSpan age)
    {
        var file = Path.Combine(data, "C", "Users", name);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[bytes]);

        return TempDirectory.Age(file, age);
    }

    private void Configure(string drive)
    {
        var directory = Path.Combine(
            _environment.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration");

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Config.xml"),
            $"<DataProtectionConfig><Target><Url>{drive}</Url></Target></DataProtectionConfig>");
    }

    /// <summary>A configured target holding one year-old version and one written today.</summary>
    private string CreateConfiguredDrive(int oldBytes = 4096, int recentBytes = 8192)
    {
        var drive = CreateDrive();
        var data = CreateData(drive);

        Configure(drive);

        if (oldBytes > 0)
        {
            CreateVersion(data, "report (2024_01_02 03_04_05 UTC).docx", oldBytes, TimeSpan.FromDays(800));
        }

        if (recentBytes > 0)
        {
            CreateVersion(data, "report (2026_09_01 03_04_05 UTC).docx", recentBytes, TimeSpan.FromMinutes(5));
        }

        return drive;
    }

    /// <summary>
    /// <c>FhManagew.exe</c> ships with every Windows 11 install, including machines that have never
    /// used the feature. Reading its presence as a hit would put a row on every machine and plan
    /// nothing on almost all of them, so presence is File History actually being set up.
    /// </summary>
    [Fact]
    public async Task ReportsNotPresentWhenFileHistoryWasNeverSetUp()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task ReportsPresentOnceFileHistoryIsSetUp()
    {
        Configure(CreateDrive());

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// §5.1 and §5.2 together, and the whole design in one assertion. The saved versions are only
    /// ever removed by asking Windows to remove them: the plan runs the documented command and
    /// targets no path at all, because the target's layout is documented nowhere and every child of
    /// it is therefore Tier 4.
    /// </summary>
    [Fact]
    public async Task RunsWindowsOwnCommandAndTargetsNoPath()
    {
        CreateConfiguredDrive();

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal("-cleanup 365 -quiet", step.Arguments);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The first of Microsoft's two conditions, measured. The command considers only versions past
    /// the retention age, so a figure counting the whole folder would promise bytes the clean will
    /// not take — §5.4's broken promise arriving from the other direction.
    /// </summary>
    [Fact]
    public async Task EstimatesOnlyWhatIsOlderThanTheRetentionAge()
    {
        CreateConfiguredDrive(oldBytes: 4096, recentBytes: 8192);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(4096, plan.EstimatedBytes);
    }

    /// <summary>
    /// The second condition cannot be measured in advance — <c>FhManagew.exe</c> reports nothing
    /// before it runs — so the figure is a ceiling rather than a forecast and has to say so. The
    /// flag is what makes the shell render it as "about".
    /// </summary>
    [Fact]
    public async Task SaysTheFigureIsAnUpperBoundRatherThanAMeasurement()
    {
        CreateConfiguredDrive();

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.Estimated.IsApproximate);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will usually free less", StringComparison.Ordinal));
    }

    /// <summary>
    /// The delta the executor reports has to subtract like from like, so the step carries Deguffer's
    /// own unguarded probe of the folder it re-measures. Subtracting the aged estimate instead would
    /// report a reclaim larger than anything that happened, because the recent versions it excludes
    /// are still there afterwards.
    /// </summary>
    [Fact]
    public async Task CarriesTheWholeFolderAsTheBeforeFigure()
    {
        CreateConfiguredDrive(oldBytes: 4096, recentBytes: 8192);

        var plan = await CreateProvider().PlanAsync();
        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal(4096 + 8192, step.MeasuredBefore!.Value.Reclaimable);
    }

    /// <summary>
    /// A target full of recent versions measures zero and is not clear, and "Already clear" is a
    /// claim about the folder. This is the one command step in the product that holds content back
    /// from its own figure, because the command itself takes an age.
    /// </summary>
    [Fact]
    public async Task ADriveWithNothingOldEnoughIsNotReportedAsClear()
    {
        CreateConfiguredDrive(oldBytes: 0, recentBytes: 8192);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(0, plan.EstimatedBytes);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// The other side of it: a target holding nothing at all really is clear, and must not claim
    /// something was held back from a folder with nothing in it.
    /// </summary>
    [Fact]
    public async Task AnEmptyTargetIsReportedAsClear()
    {
        CreateConfiguredDrive(oldBytes: 0, recentBytes: 0);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(0, plan.EstimatedBytes);
        Assert.False(plan.HasRecentContentHeldBack);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// The retention age is the user's, read at plan time so a change on the Settings page takes
    /// effect from the next preview. It reaches both the command and the figure, and the two must
    /// agree: a preview measured against one age and a command run against another describes a
    /// deletion nobody is going to perform.
    /// </summary>
    [Fact]
    public async Task TakesTheRetentionAgeFromTheSettings()
    {
        // Two versions either side of the 30 days asked for, so the figure discriminates: at the
        // shipped 365 days neither would count, and unfiltered both would.
        var drive = CreateConfiguredDrive(oldBytes: 0, recentBytes: 0);
        CreateVersion(CreateData(drive), "notes (older).txt", 2048, TimeSpan.FromDays(40));
        CreateVersion(CreateData(drive), "notes (newer).txt", 1024, TimeSpan.FromDays(20));

        var plan = await CreateProvider(AppPreferences.Default with { FileHistoryRetentionDays = 30 })
            .PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal("-cleanup 30 -quiet", step.Arguments);
        Assert.Equal(2048, plan.EstimatedBytes);
    }

    /// <summary>
    /// <c>-cleanup 0</c> keeps only the newest version of files <em>currently in the protection
    /// scope</em>, so it silently discards every version of everything the user has since moved,
    /// renamed or deleted. It is the one input this provider must never produce, and nothing
    /// validates a hand-edited <c>preferences.json</c> on the way in.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task NeverAsksWindowsToKeepOnlyTheNewestVersion(int configured)
    {
        CreateConfiguredDrive();

        var plan = await CreateProvider(AppPreferences.Default with { FileHistoryRetentionDays = configured })
            .PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal("-cleanup 1 -quiet", step.Arguments);
    }

    /// <summary>
    /// The other end of the clamp. Past ten years the age cannot be expressed as a
    /// <see cref="MinimumAge"/> at all, and building one throws rather than saturating — so a
    /// preference nobody can read would stop the whole preview rather than one row.
    /// </summary>
    [Fact]
    public async Task ClampsARetentionAgePastTheLongestWindowItCanMeasure()
    {
        CreateConfiguredDrive();

        var plan = await CreateProvider(AppPreferences.Default with { FileHistoryRetentionDays = int.MaxValue })
            .PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal($"-cleanup {FileHistoryProvider.MaximumRetentionDays} -quiet", step.Arguments);
    }

    /// <summary>
    /// §5.2's unrecognised case, at both levels a File History drive is shared at. Another person's
    /// backup and this user's backup of another machine are siblings of identical shape, and neither
    /// is this run's to trim — so both are named as survivors rather than left out silently.
    /// </summary>
    [Fact]
    public async Task NamesEveryOtherAccountAndMachineOnTheDriveAsASurvivor()
    {
        var drive = CreateConfiguredDrive();
        var theirs = CreateAnotherAccountsHistory(drive);
        var elsewhere = CreateAnotherMachinesHistory(drive);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(theirs, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(elsewhere, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// §5.6, on the folder whose loss no size comparison would show. The catalogue sits beside the
    /// versions, and removing it leaves every one of them on the drive and unreachable.
    /// </summary>
    [Fact]
    public async Task AssertsTheCatalogueBesideTheVersionsSurvived()
    {
        var drive = CreateConfiguredDrive();
        var catalogue = Directory.CreateDirectory(
            Path.Combine(CreateHistory(drive), "Configuration")).FullName;

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(catalogue, StringComparison.OrdinalIgnoreCase)
            && p.ExistedBefore);
    }

    /// <summary>
    /// §5.6's whole purpose against a command whose reach Deguffer does not control. Windows decides
    /// what <c>-cleanup</c> removes, and every assertion that the target shrank would pass just as
    /// happily if it had taken somebody else's backup with it. Only the negative catches that.
    ///
    /// <para>The test holds the check rather than a measurement of Windows: it proves that if the
    /// command ever did reach this far, Deguffer would report the run as a failure rather than as a
    /// success.</para>
    /// </summary>
    [Fact]
    public async Task ACleanupThatReachedAnotherAccountFailsTheNegative()
    {
        var drive = CreateConfiguredDrive();
        var theirs = CreateAnotherAccountsHistory(drive);

        _runner.Replying(_ =>
        {
            Directory.Delete(theirs, recursive: true);
            return new CommandOutcome(0, string.Empty, string.Empty);
        });

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(
            result.Verification.Failures,
            c => c.Path.Equals(theirs, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The backup drive is unplugged, which is the ordinary state of an external one. A zero here is
    /// about what was examined, and nothing was — so the row must not read as clear.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheBackupDriveIsNotConnected()
    {
        Configure(CreateDrive());

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// Windows' own command is the only route to removing a saved version, so without it there is
    /// nothing to offer — and a path deletion is not the fallback, because §5.2 puts the whole
    /// directory out of reach.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenWindowsOwnCommandIsMissing()
    {
        CreateConfiguredDrive();

        var provider = new FileHistoryProvider(
            new FakeUserEnvironment(_temp.Path),
            _runner,
            FakeProcessInspector.NothingRunning,
            new FakeDirectoryScanner());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>
    /// §5.2 as §7.1 reads it. Explore lets the user pick a folder out of a picture of the drive, and
    /// nothing inside a File History target is disposable by path — which is the whole reason this
    /// provider runs a command. The root is declared so Explore refuses it with a sentence rather
    /// than greying a menu item out.
    /// </summary>
    [Fact]
    public void DeclaresTheTargetAsARootWithNoDisposableChildren()
    {
        var drive = CreateConfiguredDrive();

        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Path.Combine(drive, "FileHistory"), root.Path);
        Assert.False(root.Recognises("Data"));
        Assert.False(root.Recognises(FakeUserEnvironment.Account));
    }

    /// <summary>
    /// The target is remembered for the life of a pass (G4), so a backup drive plugged in while the
    /// app was open has to be picked up on the next preview like every other cached view of the
    /// machine.
    /// </summary>
    [Fact]
    public async Task InvalidatingReachesTheTargetLookup()
    {
        var drive = CreateDrive();
        Configure(drive);

        var provider = CreateProvider();
        Assert.True((await provider.PlanAsync()).IsEmpty);

        CreateData(drive);
        CreateVersion(Path.Combine(CreateHistory(drive), "Data"), "old.txt", 4096, TimeSpan.FromDays(800));
        provider.InvalidateCaches();

        Assert.Equal(4096, (await provider.PlanAsync()).EstimatedBytes);
        Assert.Equal(1, _environment.InvalidateCount);
    }
}
