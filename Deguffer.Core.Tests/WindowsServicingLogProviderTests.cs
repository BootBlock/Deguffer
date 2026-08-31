using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The second provider to reach into the Windows directory, and the one whose targets are nested
/// deepest — <c>System32\LogFiles\WMI\RtBackup</c> is four levels down.
///
/// What has to hold is that reaching that deep never widens what is reachable. Every directory
/// passed through on the way is a container rather than a target, is checked for being a link, and
/// is asserted to have survived; and §9's exclusions in the same parent are still out of reach.
/// </summary>
public sealed class WindowsServicingLogProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public WindowsServicingLogProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Windows => _system.WindowsDirectory;

    private WindowsServicingLogProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A log directory with a file in it, so it measures above zero and is selectable.</summary>
    private string Populate(string name, int bytes = 4096, string file = "log.log")
    {
        var directory = Path.Combine(Windows, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, file), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineHoldingNoneOfThem()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>The Windows directory is on every machine, so its existence cannot be presence.</summary>
    [Fact]
    public async Task TheWindowsDirectoryExistingIsNotPresence()
    {
        Populate("WinSxS", file: "component.manifest");
        Populate(Path.Combine("Logs", "DISM"), file: "dism.log");

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansOneStepPerDeclaredLocationThatIsThere()
    {
        var cbs = Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");
        var windowsUpdate = Populate(Path.Combine("Logs", "WindowsUpdate"), file: "trace.etl");
        var panther = Populate("Panther", file: "setupact.log");
        var rtBackup = Populate(Path.Combine("System32", "LogFiles", "WMI", "RtBackup"), file: "EtwRT.etl");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { cbs, windowsUpdate, panther, rtBackup }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.UserData, plan.Tier);
    }

    /// <summary>
    /// The containers on the way down. Each is left standing while something inside it is removed,
    /// which is the one case where "we did not recognise that" would be an actively false thing to
    /// say — the same correction Chromium's <c>Cache</c> forced. So each carries its own reason and
    /// is asserted individually.
    /// </summary>
    [Fact]
    public async Task EveryDirectoryPassedThroughIsAContainerRatherThanATarget()
    {
        Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");
        Populate(Path.Combine("System32", "LogFiles", "WMI", "RtBackup"), file: "EtwRT.etl");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] containers =
        [
            Windows,
            Path.Combine(Windows, "Logs"),
            Path.Combine(Windows, "System32"),
            Path.Combine(Windows, "System32", "LogFiles"),
            Path.Combine(Windows, "System32", "LogFiles", "WMI"),
        ];

        foreach (var container in containers)
        {
            Assert.DoesNotContain(container, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(container, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(containers, c => Assert.True(Directory.Exists(c), $"{c} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 against the operating system's own directory, proved by running a plan.
    ///
    /// §9 keeps <c>WinSxS</c> and <c>Windows\Installer</c> out of the product, and an over-broad rule
    /// passes every positive assertion — so the evidence that this one is not over-broad is that they
    /// are still there, along with a sibling of a target that the declaration never named.
    /// </summary>
    [Fact]
    public async Task TheSection9ExclusionsAndAnUnnamedSiblingSurviveARun()
    {
        var cbs = Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");

        var winSxS = Populate("WinSxS", file: "component.manifest");
        var installer = Populate("Installer", file: "patch.msp");

        // §5.2's unrecognised case in the form this provider can have one. Nothing is enumerated
        // here, so what has to hold is that a directory the table does not name — including one
        // sitting inside a container it does reach into — is never touched.
        var dism = Populate(Path.Combine("Logs", "DISM"), file: "dism.log");
        var config = Populate(Path.Combine("System32", "config"), file: "SOFTWARE");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] mustSurvive = [Windows, winSxS, installer, dism, config];

        Assert.Equal([cbs], plan.TargetedPaths);

        foreach (var asserted in new[] { Windows, winSxS, installer })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(asserted, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cbs));
        Assert.All(mustSurvive, path => Assert.True(Directory.Exists(path), $"{path} was destroyed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7's age, and the reason it is not the directory's own timestamp.
    ///
    /// A log is appended to, which moves the file and leaves the parent untouched. Reading the
    /// directory alone would report a servicing log being written right now as months old — and
    /// "somebody is diagnosing a failed update at this moment" is exactly the case Tier 3 and the age
    /// column exist to put in front of the user.
    /// </summary>
    [Fact]
    public async Task TheAgeComesFromTheNewestFileInsideRatherThanTheDirectory()
    {
        var cbs = Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");

        var appended = DateTime.UtcNow.AddMinutes(-3);
        File.SetLastWriteTimeUtc(Path.Combine(cbs, "CBS.log"), appended);
        Directory.SetLastWriteTimeUtc(cbs, DateTime.UtcNow.AddDays(-240));

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        Assert.NotNull(step.LastWritten);
        Assert.Equal(appended, step.LastWritten!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Every step here is under the Windows directory, so none of them can run unelevated. The plan
    /// says so rather than letting the removal discover it, and the offer that fixes it fires.
    /// </summary>
    [Fact]
    public async Task EveryStepSaysItNeedsAdministratorRights()
    {
        Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");
        Populate("Panther", file: "setupact.log");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.RequiresElevation);
        Assert.All(plan.Steps, s => Assert.True(s.RequiresElevation));
        Assert.Contains(plan.Notes, n => n.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase));

        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false, [new Finding(provider, IsPresent: true, plan)]));
    }

    /// <summary>
    /// §5.3, stated unconditionally rather than only when a named process is up. The WMI service
    /// holds its own trace files open and is always running, so reclaiming less than the size shown
    /// is the expected outcome here — and a user who was not told that reads it as a failure.
    /// </summary>
    [Fact]
    public async Task SaysPlainlyThatSomeOfTheseAreAlwaysHeldOpen()
    {
        Populate(Path.Combine("System32", "LogFiles", "WMI", "RtBackup"), file: "EtwRT.etl");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("held open", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.3's other half: what is genuinely held open stays, and the step reports that rather than
    /// claiming a clean sweep. The directory survives too, which is correct — it still has a file.
    /// </summary>
    [Fact]
    public async Task AHeldLogIsLeftInPlaceAndCounted()
    {
        var cbs = Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");
        File.WriteAllBytes(Path.Combine(cbs, "CbsPersist.log"), new byte[2048]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        using var held = new FileStream(
            Path.Combine(cbs, "CBS.log"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(1, result.SkippedCount);
        Assert.True(result.BytesReclaimed > 0, "the log that was not held should still have gone");
        Assert.True(File.Exists(Path.Combine(cbs, "CBS.log")), "a held log was deleted");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A step that achieved nothing names administrator rights as a possible cause.
    ///
    /// The outcome cannot tell the two apart: an unelevated delete under the Windows directory is
    /// refused file by file, which arrives as the same skip a locked file produces. Reporting only
    /// §5.3's "in use" would send the user looking for a process to close that is not there, and
    /// that is the whole of what an unelevated run of this provider would say for itself.
    /// </summary>
    [Fact]
    public async Task AStepThatAchievedNothingNamesAdministratorRightsAsACause()
    {
        var cbs = Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        using var held = new FileStream(
            Path.Combine(cbs, "CBS.log"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains("administrator", outcome.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(cbs));
    }

    /// <summary>
    /// A junction at a level the declaration only passes through. <c>Logs</c> is a container for two
    /// targets, so a check on the final path alone would walk straight through a junctioned one and
    /// delete in a tree the plan never named.
    /// </summary>
    [Fact]
    public async Task AJunctionedContainerIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "CBS"));

        var bystander = Path.Combine(outside, "CBS", "irreplaceable.log");
        File.WriteAllBytes(bystander, new byte[4096]);

        Directory.CreateSymbolicLink(Path.Combine(Windows, "Logs"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Logs", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "planning looked through a junctioned container");
    }

    /// <summary>§7: Tier 3, so never pre-selected and never run without the typed phrase.</summary>
    [Fact]
    public async Task ATier3PlanIsNeverPreSelectedAndDemandsTheTypedPhrase()
    {
        Populate("Panther", file: "setupact.log");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);

        var requirement = ConfirmationRequirement.For(plan);

        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Equal(provider.Name, requirement.RequiredPhrase);
        Assert.False(requirement.IsSatisfiedBy([new Confirmation(provider.Id)]));
    }

    /// <summary>
    /// §6.3. <c>Panther</c> keeps a whole setup tree, and <c>MAX_PATH</c> is reachable inside one. A
    /// truncation would be a partial deletion, which is the failure §6.3 exists to prevent.
    ///
    /// A crash guard rather than a discriminating test:
    /// <see cref="DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/> is what
    /// proves the form of the path.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var panther = Path.Combine(Windows, "Panther");
        Directory.CreateDirectory(panther);

        var deep = panther;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var file = Path.Combine(deep, "setupact.log");
        Assert.True(file.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(file), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(panther, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "a setup log past MAX_PATH was not measured.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(file), "a file past MAX_PATH survived the removal.");
        Assert.True(Directory.Exists(Windows));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3's warning for the servicing stack, which holds the log it is currently writing. A user
    /// mid-update should see that before they type the phrase.
    /// </summary>
    [Fact]
    public async Task WarnsWhileTheServicingStackIsRunning()
    {
        Populate(Path.Combine("Logs", "CBS"), file: "CBS.log");

        var provider = new WindowsServicingLogProvider(
            _environment,
            new FakeProcessRunner(),
            new FakeProcessInspector("TiWorker"),
            system: _system);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning &&
            n.Message.Contains("TiWorker", StringComparison.Ordinal));
    }

    /// <summary>The declaration itself: the Windows directory is never among its own targets.</summary>
    [Fact]
    public void TheWindowsDirectoryIsNeverAmongTheDeclaredTargets()
    {
        var root = Assert.Single(CreateProvider().Roots);

        var targets = root.Locations.Select(l => Path.Combine(root.Path, l.RelativePath)).ToList();

        Assert.Equal(Windows, root.Path);
        Assert.DoesNotContain(root.Path, targets, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(WindowsSystemRoot.Exclusions, root.ProtectedNames);
        Assert.True(root.RequiresElevation);
    }
}
