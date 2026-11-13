using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// #691: an administrator's pass plans every row the update hold governs against what Windows servicing
/// was doing before the pass started DISM's analysis, not against what the analysis made Windows show.
///
/// <para>For as long as DISM runs, Windows runs its servicing stack and keeps its servicing session
/// key, one of the keys that say a restart is owed. Read live after DISM starts, both would hold the
/// component store, the driver store and Windows.old on every elevated pass.</para>
/// </summary>
public sealed class PlanningServicingTests : IDisposable
{
    private static readonly TimeSpan PastTheUninstallWindow = TimeSpan.FromDays(20);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeWindowsServicing _servicing = FakeWindowsServicing.Settled;
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;
    private readonly string _driverStore;
    private readonly string _windowsOld;

    public PlanningServicingTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).AsAdministrator();
        _system = new FakeSystemDirectories(_temp.Path);
        _temp.CreateFile(4096, "Windows", "WinSxS", "Manifests", "component.manifest");
        _driverStore = Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository");
        _windowsOld = Path.Combine(_system.SystemDrive, "Windows.old");
        Directory.CreateDirectory(_windowsOld);
        File.WriteAllBytes(Path.Combine(_windowsOld, "file.bin"), new byte[4096]);
        Aged(_windowsOld, PastTheUninstallWindow);
    }

    public void Dispose() => _temp.Dispose();

    private string Dism => NativeSystemTool.In(_system, "Dism.exe");

    /// <summary>
    /// DISM's analysis, which makes Windows show what it shows for any DISM session: its servicing
    /// stack running and its session key written. Another row's use check reads the process table
    /// again while it runs, as a check past <see cref="IUseCheck.ReadingLimit"/> does.
    /// </summary>
    private FakeProcessRunner AnalysisWakingTheServicingStack() =>
        new FakeProcessRunner().Replying(Dism, _ =>
        {
            var now = DateTimeOffset.UtcNow;
            _servicing.ServicingSessionWritten = now;
            _inspector.WithRunning("TrustedInstaller", now).WithRunning("TiWorker", now).PassTheReadingLimit();
            _inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

            return new CommandOutcome(0, ComponentStoreReportTests.Sample, string.Empty);
        });

    /// <summary>
    /// The component store's rows first, so their pass start runs DISM before any row plans, then the
    /// driver store and Windows.old, all on one machine's servicing and process table, as the app
    /// builds them.
    /// </summary>
    private ICleanupProvider[] Rows(IProcessRunner dism)
    {
        var calls = OwnServicingCalls.For(_servicing);
        var analysis = new ComponentStoreAnalysis(_system, dism, calls);

        return
        [
            new ComponentStoreCleanupProvider(_environment, dism, _inspector, _system, _servicing, analysis, servicingCalls: calls),
            new ComponentStoreResetBaseProvider(_environment, dism, _inspector, _system, _servicing, analysis, servicingCalls: calls),
            new DriverStoreProvider(
                new FakeDiskCleanupHandlers((_, _) => new DiskCleanupOutcome(Ran: true)),
                _environment,
                new FakeProcessRunner(),
                _inspector,
                system: _system,
                servicing: _servicing,
                store: new FakeDriverStore(Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01"))),
            new PreviousWindowsInstallationProvider(
                FakeDiskCleanupHandlers.Windows(), _environment, new FakeProcessRunner(), _inspector, system: _system, servicing: _servicing),
        ];
    }

    /// <summary>
    /// The analysis the pass started is read, and both component store rows offer their command. The
    /// driver store and Windows.old are offered too, because nothing said an update was going on
    /// before Deguffer's own DISM call.
    /// </summary>
    [Fact]
    public async Task TheAnalysisThePassStartedHoldsNoneOfTheRowsTheHoldGoverns()
    {
        var dism = AnalysisWakingTheServicingStack();
        var rows = Rows(dism);

        var findings = await new CleanupPlanner(rows).PlanAllAsync();

        // The premise: by the time the rows planned, Windows showed a session key and a servicing stack.
        Assert.NotNull(_servicing.ServicingSessionWritten);
        Assert.Single(_inspector.FindRunning(["TiWorker"]));
        Assert.Single(dism.Invocations);

        Assert.All(rows, row =>
        {
            var plan = Assert.Single(findings, finding => finding.Provider == row).Plan!;
            Assert.False(plan.WaitsForAnUpdate, $"{row.Name} was held: {string.Join(" ", plan.Notes.Select(n => n.Message))}");
            Assert.NotEmpty(plan.Steps);
        });
    }

    /// <summary>
    /// #450: the driver store starts its listing as the pass begins too, and ahead of the component store
    /// in the planner's list it may read the machine first. Whichever reads first, the reading is taken
    /// before DISM starts, so the analysis holds back neither the driver store nor any other row.
    /// </summary>
    [Fact]
    public async Task ADriverStoreStartedAheadOfTheAnalysisIsPlannedAgainstTheMachineBeforeIt()
    {
        var dism = AnalysisWakingTheServicingStack();
        var rows = Rows(dism).Reverse().ToArray();

        var findings = await new CleanupPlanner(rows).PlanAllAsync();

        Assert.Single(dism.Invocations);
        Assert.All(rows, row =>
        {
            var plan = Assert.Single(findings, finding => finding.Provider == row).Plan!;
            Assert.False(plan.WaitsForAnUpdate, $"{row.Name} was held: {string.Join(" ", plan.Notes.Select(n => n.Message))}");
        });
    }

    /// <summary>
    /// The unrecognised case: a restart already owed when the pass began is an update's for all the
    /// pass can tell, so it holds every row, and DISM is never asked.
    /// </summary>
    [Fact]
    public async Task ARestartOwedBeforeThePassBeganHoldsEveryRow()
    {
        _servicing.IsRestartPending = true;
        var dism = AnalysisWakingTheServicingStack();
        var rows = Rows(dism);

        var findings = await new CleanupPlanner(rows).PlanAllAsync();

        Assert.Empty(dism.Invocations);
        Assert.All(rows, row => Assert.True(Assert.Single(findings, finding => finding.Provider == row).Plan!.WaitsForAnUpdate, row.Name));
        Assert.True(Directory.Exists(_windowsOld));
    }

    /// <summary>
    /// A servicing session key from before the pass is another DISM session's, which may be an
    /// update's, and holds every row, the component store's own included.
    /// </summary>
    [Fact]
    public async Task AServicingSessionUnderWayBeforeThePassBeganHoldsEveryRow()
    {
        _servicing.ServicingSessionWritten = DateTimeOffset.UtcNow.AddMinutes(-1);
        var dism = AnalysisWakingTheServicingStack();
        var rows = Rows(dism);

        var findings = await new CleanupPlanner(rows).PlanAllAsync();

        Assert.Empty(dism.Invocations);
        Assert.All(rows, row => Assert.True(Assert.Single(findings, finding => finding.Provider == row).Plan!.WaitsForAnUpdate, row.Name));
    }

    /// <summary>
    /// The reading is the pass's, never the app's: a restart owed at one pass and gone by the next is
    /// not remembered into the next.
    /// </summary>
    [Fact]
    public async Task EachPassReadsTheMachineAfresh()
    {
        _servicing.IsRestartPending = true;
        var rows = Rows(AnalysisWakingTheServicingStack());
        var planner = new CleanupPlanner(rows);
        Assert.All(await planner.PlanAllAsync(), finding => Assert.True(finding.Plan!.WaitsForAnUpdate));

        _servicing.IsRestartPending = false;
        var next = await planner.PlanAllAsync();

        Assert.All(next, finding => Assert.False(finding.Plan!.WaitsForAnUpdate, finding.Provider.Name));
    }

    /// <summary>One reading per machine, so no row the hold governs can read it later in the pass than another.</summary>
    [Fact]
    public void EveryProviderOnOneMachineSharesOneReading()
    {
        Assert.Same(PlanningServicing.For(_servicing, _inspector), PlanningServicing.For(_servicing, _inspector));
        Assert.NotSame(PlanningServicing.For(_servicing, _inspector), PlanningServicing.For(_servicing, FakeProcessInspector.NothingRunning));
        Assert.NotSame(PlanningServicing.For(_servicing, _inspector), PlanningServicing.For(FakeWindowsServicing.Settled, _inspector));
    }

    /// <summary>A third-party driver package staged in the store, holding one file.</summary>
    private DriverPackage Staged(string published, string date)
    {
        var folder = Path.Combine(_driverStore, $"wifi.inf_amd64_{Path.GetFileNameWithoutExtension(published)}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "wifi.inf"), new byte[4096]);

        return new DriverPackage(
            published,
            "wifi.inf",
            "Example Networks",
            DriverStoreProviderTests.NetClass,
            ExtensionId: null,
            DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            new Version(1, 0, 0, 0),
            InUse: false,
            folder);
    }

    private static void Aged(string directory, TimeSpan age)
    {
        var when = DateTime.UtcNow - age;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, when);
            File.SetCreationTimeUtc(file, when);
        }

        Directory.SetLastWriteTimeUtc(directory, when);
        Directory.SetCreationTimeUtc(directory, when);
    }
}
