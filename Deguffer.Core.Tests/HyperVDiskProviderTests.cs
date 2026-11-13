using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Hyper-V's machines, disks and checkpoints, as Hyper-V's own answer lists them, reported with Hyper-V's
/// route and no step, and an answer Hyper-V would not give read as unknown, never as no machines.
///
/// <para>Windows PowerShell is a <see cref="FakeProcessRunner"/> that replies as the query's JSON does,
/// and throws for any other program, so a test with no reply also proves nothing was run. Every disk is a
/// file in the test's own folder, and the account is a member of Hyper-V Administrators unless a test says
/// otherwise.</para>
/// </summary>
public sealed class HyperVDiskProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly HyperVMachines _machines;

    public HyperVDiskProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).AsHyperVAdministrator();
        _system = new FakeSystemDirectories(_temp.Path);
        _machines = new HyperVMachines(_system, _environment, new FakeProcessRunner());
    }

    public void Dispose() => _temp.Dispose();

    private HyperVDiskProvider Provider(FakeProcessRunner runner, IUserEnvironment? environment = null) =>
        new(_system, environment ?? _environment, runner, FakeProcessInspector.NothingRunning);

    private void InstallModule() => Directory.CreateDirectory(Path.Combine(_machines.Module, "2.0.0.0"));

    private FakeProcessRunner Answering(string json, int exitCode = 0) =>
        new FakeProcessRunner().Replying(_machines.PowerShell, _ => new CommandOutcome(exitCode, json, string.Empty));

    private string Disk(string name, int bytes = 4096)
    {
        var folder = Path.Combine(_temp.Path, "Hyper-V", "Dev");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static string Json(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static string Text(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    [Fact]
    public async Task WithoutTheHyperVModuleNothingIsRunAndThereIsNoRow()
    {
        var runner = new FakeProcessRunner();

        Assert.False(await Provider(runner).IsPresentAsync());
        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// <c>Get-VM</c> has been reported (by Altaro) to list nothing, rather than fail, for an account Hyper-V does not
    /// trust, so an account that is neither elevated nor a member of Hyper-V Administrators is told whether it
    /// has machines is not known, and Windows PowerShell is never started to learn what the token already says.
    /// Mutation: dropping the role check runs the query, and taking the refusal for no machines leaves no row.
    /// </summary>
    [Fact]
    public async Task AnAccountHyperVWillNotAnswerIsToldItIsNotKnownWithNothingStarted()
    {
        InstallModule();
        var runner = new FakeProcessRunner();
        var provider = Provider(runner, new FakeUserEnvironment(_temp.Path));

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        var refusal = Assert.Single(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains("elevated PowerShell or a member of the Hyper-V Administrators group", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("is not known", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(plan.Steps);
        Assert.Empty(await provider.RegisteredStorageAsync());
        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// The two accounts Hyper-V answers are asked, an elevated one and an unelevated member of Hyper-V
    /// Administrators. Mutation: checking only <see cref="IUserEnvironment.IsAdministrator"/> refuses the member.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnElevatedAccountOrAMemberOfHyperVAdministratorsIsAsked(bool elevated)
    {
        InstallModule();
        var account = new FakeUserEnvironment(_temp.Path);
        var environment = elevated ? account.AsAdministrator() : account.AsHyperVAdministrator();
        var disk = Disk("Dev.vhdx");
        var runner = Answering(Json(
            $$"""{"machines":[{"name":"Dev","state":"Off","checkpoints":0,"attached":["{{disk}}"],"kept":[],"unfollowed":[]}]}"""));

        Assert.Equal(disk, Assert.Single(await Provider(runner, environment).RegisteredStorageAsync()).Path);
        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData("""{"error":"Hyper-V was unable to find a virtual machine management service."}""", 0, "unable to find a virtual machine management service")]
    [InlineData("not json", 0, "could not be read")]
    [InlineData("[]", 0, "could not be read")]
    [InlineData("""{"machines":3}""", 0, "could not be read")]
    [InlineData("", 1, "could not ask Hyper-V")]
    public async Task AHyperVThatDoesNotAnswerIsSaidAndNeverTakenForNoMachines(string answer, int exitCode, string said)
    {
        InstallModule();
        var provider = Provider(Answering(answer, exitCode));

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(said, Text(await provider.PlanAsync()), StringComparison.Ordinal);
    }

    /// <summary>A module folder Windows will not describe may be there, so Hyper-V is asked anyway.</summary>
    [Fact]
    public async Task AModuleFolderWindowsWillNotDescribeIsAskedRatherThanTakenForAbsent()
    {
        InstallModule();
        using var denied = new DeniedDirectory(Path.GetDirectoryName(_machines.Module)!);
        var runner = Answering("""{"machines":[]}""");

        await Provider(runner).IsPresentAsync();

        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task AnAccountWithNoMachinesHasNoRow()
    {
        InstallModule();

        Assert.False(await Provider(Answering("""{"machines":[]}""")).IsPresentAsync());
    }

    /// <summary>
    /// A machine with checkpoints: the disk it runs from, the checkpoint's disk under it, both measured and
    /// registered, and Hyper-V's route in order, checkpoints first.
    /// </summary>
    [Fact]
    public async Task AMachinesDiskAndItsCheckpointsDiskAreReportedWithHyperVsRouteInOrder()
    {
        InstallModule();
        var current = Disk("Dev_7F3A.avhdx", 8192);
        var base_ = Disk("Dev.vhdx", 4096);
        var provider = Provider(Answering(Json(
            $$"""{"machines":[{"name":"Dev","state":"Saved","checkpoints":2,"attached":["{{current}}"],"kept":["{{base_}}","{{current}}"],"unfollowed":[]}]}""")));

        var plan = await provider.PlanAsync();
        var text = Text(plan);

        Assert.True(plan.ReportsOnly);
        Assert.Empty(plan.Steps);
        Assert.Contains($"The Hyper-V virtual machine 'Dev': {current}.", text, StringComparison.Ordinal);
        Assert.Contains($"A checkpoint of the Hyper-V virtual machine 'Dev': {base_}.", text, StringComparison.Ordinal);
        Assert.Contains($"2 virtual disks take {FreeSpace.Format(12288)} on this drive.", text, StringComparison.Ordinal);
        Assert.Contains("The machine has 2 checkpoints", text, StringComparison.Ordinal);
        Assert.Contains("The machine is saved", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("Remove-VMCheckpoint", StringComparison.Ordinal) < text.IndexOf("Optimize-VHD", StringComparison.Ordinal),
            "The route deletes checkpoints before it compacts the disk.");
        Assert.Contains(VirtualDiskRoutes.HyperVModuleNeeds, text, StringComparison.Ordinal);

        var registered = (await provider.RegisteredStorageAsync()).Select(storage => storage.Path).ToList();
        Assert.Equal([current, base_], registered);
    }

    /// <summary>
    /// A path Hyper-V gives with the extended-length prefix is the same disk as its plain form, so it is
    /// named once and compares equal to the path Explore draws.
    /// </summary>
    [Fact]
    public void ADiskHyperVNamesWithTheDevicePrefixIsThePlainPath()
    {
        var plain = Path.Combine(_temp.Path, "Hyper-V", "Dev", "Dev.vhdx");

        var inventory = HyperVMachines.Parse(Json(
            $$"""{"machines":[{"name":"Dev","state":"Off","checkpoints":0,"attached":["\\?\{{plain}}"],"kept":["{{plain}}"],"unfollowed":[]}]}"""));

        var disk = Assert.Single(inventory.Disks);
        Assert.Equal(plain, disk.Path);
        Assert.False(disk.Path.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.Equal(MachineDiskRole.Current, Assert.IsType<HyperVDisk>(disk.Owner).Role);
    }

    /// <summary>
    /// A warning a module writes while it loads goes to standard output ahead of the JSON, so the answer is
    /// the last line.
    /// </summary>
    [Fact]
    public async Task AWarningAheadOfTheAnswerIsPassedOver()
    {
        InstallModule();
        var disk = Disk("Dev.vhdx");
        var runner = Answering(Json(
            $$"""
            WARNING: The names of some imported commands from the module 'Hyper-V' include unapproved verbs.
            {"machines":[{"name":"Dev","state":"Off","checkpoints":0,"attached":["{{disk}}"],"kept":[],"unfollowed":[]}]}
            """));

        Assert.Equal(disk, Assert.Single(await Provider(runner).RegisteredStorageAsync()).Path);
    }

    [Fact]
    public void AParentHyperVWouldNotDescribeIsSaidRatherThanHidden()
    {
        var disk = Path.Combine(_temp.Path, "Dev.avhdx");

        var inventory = HyperVMachines.Parse(Json(
            $$"""{"machines":[{"name":"Dev","state":"Off","checkpoints":1,"attached":["{{disk}}"],"kept":[],"unfollowed":["{{disk}}"]}]}"""));

        Assert.Contains(disk, Assert.Single(inventory.Problems), StringComparison.Ordinal);
    }

    /// <summary>The query only reads, and reaches Windows PowerShell whole, with nothing for a command line to unquote.</summary>
    [Fact]
    public async Task TheQueryRunsWindowsPowerShellAndChangesNothing()
    {
        InstallModule();
        var runner = Answering("""{"machines":[]}""");

        await Provider(runner).IsPresentAsync();

        var run = Assert.Single(runner.Invocations);
        Assert.Equal(Path.Combine(_system.WindowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), run.FileName);

        // Read in the console's OEM code page, a disk under a non-ASCII folder would be registered at a path
        // that is not there, and protected as absent.
        Assert.Equal(ToolOutputEncoding.Utf8, run.Run.Output);
        Assert.StartsWith("-NoProfile -NonInteractive -EncodedCommand ", run.Arguments, StringComparison.Ordinal);

        var query = Encoding.Unicode.GetString(Convert.FromBase64String(run.Arguments.Split(' ')[^1]));
        Assert.Equal(HyperVMachines.Query, query);
        Assert.Contains("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false", query, StringComparison.Ordinal);

        foreach (var verb in (string[])["Remove-", "Optimize-", "Set-", "Stop-", "Start-", "Merge-", "Restore-", "Dismount-", "Mount-"])
        {
            Assert.DoesNotContain(verb, query, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The run-wide refusal for a Hyper-V disk: a row whose location holds one leaves it, and the run's §5.6
    /// negative names it as having survived. Only that row is scanned, so neither may depend on this row.
    /// </summary>
    [Fact]
    public async Task ARowWhoseLocationHoldsAHyperVDiskLeavesItAndTheRunProvesIt()
    {
        InstallModule();
        var caches = Path.Combine(_environment.UserProfile, ".gradle", "caches");
        var machines = Path.Combine(caches, "vms");
        Directory.CreateDirectory(machines);
        var disk = Path.Combine(machines, "Dev.vhdx");
        File.WriteAllBytes(disk, new byte[4096]);
        File.WriteAllBytes(Path.Combine(caches, "modules.bin"), new byte[4096]);

        var gradle = new GradleCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        Assert.Contains((await gradle.PlanAsync()).TargetedPaths, path => LongPath.Contains(path, disk));

        var report = Provider(Answering(Json(
            $$"""{"machines":[{"name":"Dev","state":"Off","checkpoints":0,"attached":["{{disk}}"],"kept":[],"unfollowed":[]}]}""")));
        var planner = new CleanupPlanner([gradle, report], volumes: new FakeVolumeInventory());
        var cache = Assert.Single(await planner.PlanAsync([gradle]));

        Assert.DoesNotContain(cache.Plan!.TargetedPaths, path => LongPath.Contains(path, disk));

        var results = await planner.ExecuteAsync([cache]);

        Assert.True(File.Exists(disk), "a row whose location held a Hyper-V disk deleted it");
        Assert.All(results, result => Assert.True(result.Verification!.Passed, result.Verification.Summary));
        Assert.Contains(
            results,
            result => result.ProviderId == report.Id
                && result.Verification!.Checks.Any(check => check.Subject == disk && check.Outcome == VerificationOutcome.Survived));
    }
}
