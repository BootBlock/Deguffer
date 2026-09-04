using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider to reach into the Windows directory, and the first to target a single file.
///
/// §5.2 is stricter here than anywhere else in the suite and is enforced differently: nothing is
/// enumerated, so there is no unrecognised child to classify — the provider names absolute paths and
/// everything else is unreachable by construction. What that has to be shown to mean is that a rule
/// reaching into <c>C:\Windows</c> cannot reach §9's exclusions, which is what
/// <see cref="TheSection9ExclusionsSurviveARunAndAreAssertedRatherThanMerelyOmitted"/> executes a
/// plan to establish.
/// </summary>
public sealed class CrashDumpProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public CrashDumpProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Windows => _system.WindowsDirectory;

    private string ProgramData => _system.ProgramData;

    private string WerFolder => Path.Combine(ProgramData, "Microsoft", "Windows", "WER");

    private CrashDumpProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A directory with one file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096, string name = "dump.dmp")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNothingHasEverCrashed()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// Every root here exists on every Windows machine, so a root existing must not read as presence
    /// — that would report this source everywhere and then plan nothing on most machines. The same
    /// rule the shader caches needed for a vendor directory.
    /// </summary>
    [Fact]
    public async Task ARootExistingIsNotPresence()
    {
        Directory.CreateDirectory(WerFolder);
        Populate(Path.Combine(Windows, "WinSxS"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansOneStepPerDeclaredLocationThatIsThere()
    {
        var crashDumps = Populate(Path.Combine(_environment.LocalAppData, "CrashDumps"));
        var archive = Populate(Path.Combine(WerFolder, "ReportArchive"));
        var queue = Populate(Path.Combine(WerFolder, "ReportQueue"));
        var minidump = Populate(Path.Combine(Windows, "Minidump"));
        var live = Populate(Path.Combine(Windows, "LiveKernelReports"));

        var memoryDump = Path.Combine(Windows, "MEMORY.DMP");
        File.WriteAllBytes(memoryDump, new byte[65536]);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { crashDumps, archive, queue, live, minidump, memoryDump }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.True(plan.EstimatedBytes > 65536);
    }

    /// <summary>
    /// §3, and a correction to the survey that proposed these as Tier 1. Nothing re-creates a crash
    /// dump, because the crash does not happen again to order — which is the property that puts a
    /// thing in Tier 3 rather than Tier 1, and Tier 3 is what decides the row is never pre-selected
    /// and asks §7's typed phrase of anyone who has left that setting on.
    /// </summary>
    [Fact]
    public async Task ATier3PlanIsNeverPreSelectedAndDemandsTheTypedPhrase()
    {
        Populate(Path.Combine(_environment.LocalAppData, "CrashDumps"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var finding = new Finding(provider, IsPresent: true, plan);

        Assert.Equal(SafetyTier.UserData, provider.Tier);
        Assert.False(finding.IsPreSelectedByDefault);

        var requirement = ConfirmationRequirement.For(plan);

        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Equal(provider.Name, requirement.RequiredPhrase);
        Assert.False(requirement.IsSatisfiedBy([new Confirmation(provider.Id)]));
        Assert.True(requirement.IsSatisfiedBy([new Confirmation(provider.Id, provider.Name)]));
    }

    /// <summary>
    /// The whole of §5.2 against the most dangerous parent Deguffer has reached into, proved by
    /// running a plan rather than by reading the declaration.
    ///
    /// §9 keeps <c>WinSxS</c> and <c>Windows\Installer</c> out of the product because the failure
    /// modes are a broken uninstall and an unbootable rollback. An over-broad rule passes every
    /// positive assertion, so the only evidence that this one is not over-broad is that those paths,
    /// the installer package cache beside <c>%PROGRAMDATA%</c>'s own targets, an unrecognised
    /// neighbour, and both roots themselves are all still there afterwards.
    /// </summary>
    [Fact]
    public async Task TheSection9ExclusionsSurviveARunAndAreAssertedRatherThanMerelyOmitted()
    {
        var minidump = Populate(Path.Combine(Windows, "Minidump"));
        Populate(Path.Combine(WerFolder, "ReportQueue"));

        var winSxS = Populate(Path.Combine(Windows, "WinSxS"), name: "component.manifest");
        var installer = Populate(Path.Combine(Windows, "Installer"), name: "patch.msp");
        var packageCache = Populate(Path.Combine(ProgramData, "Package Cache"), name: "bundle.exe");

        // §5.2's unrecognised case, in the form this provider can have one: a neighbour it never
        // named. There is no classification to get wrong here, so what has to hold is that a
        // directory the table does not mention is never reached at all.
        var unnamed = Populate(Path.Combine(Windows, "SoftwareDistribution"), name: "download.cab");
        var werTemp = Populate(Path.Combine(WerFolder, "Temp"), name: "partial.wer");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] mustSurvive = [Windows, ProgramData, winSxS, installer, packageCache, unnamed, werTemp];

        foreach (var spared in mustSurvive)
        {
            Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }

        // The four §9 and root paths are named rather than merely absent, so the run produces
        // evidence about them. The two neighbours are unreachable by construction and so carry no
        // assertion — which is why they are executed against below.
        foreach (var asserted in new[] { Windows, ProgramData, winSxS, installer, packageCache })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(asserted, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(minidump));

        foreach (var spared in mustSurvive)
        {
            Assert.True(Directory.Exists(spared), $"{Path.GetFileName(spared)} was destroyed");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>MEMORY.DMP</c> is a single file, and the reason a second kind of deletion step exists. Its
    /// age is its own write time, which is the moment the machine stopped — the most useful date on
    /// this whole plan.
    /// </summary>
    [Fact]
    public async Task TheKernelDumpIsAFileStepSizedAndDatedFromTheFileItself()
    {
        var memoryDump = Path.Combine(Windows, "MEMORY.DMP");
        File.WriteAllBytes(memoryDump, new byte[131072]);

        var stopped = DateTime.UtcNow.AddDays(-11);
        File.SetLastWriteTimeUtc(memoryDump, stopped);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps);
        var file = Assert.IsType<DeleteFileStep>(step);

        Assert.Equal(memoryDump, file.Path);
        Assert.Equal(131072, file.EstimatedBytes);
        Assert.NotNull(file.LastWritten);
        Assert.Equal(stopped, file.LastWritten!.Value, TimeSpan.FromSeconds(1));

        var result = await CreateProvider().ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(131072, result.BytesReclaimed);
        Assert.False(File.Exists(memoryDump));
        Assert.True(Directory.Exists(Windows), "the Windows directory went with the dump inside it");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The claim that is not <see cref="FallbackReason.NotElevated"/>: this one is about whether the
    /// removal can happen at all. Only the profile's own folder is the user's to clear, and a plan
    /// that failed to distinguish the two would either fail silently during execution or drop the
    /// locations from an unelevated preview altogether.
    /// </summary>
    [Fact]
    public async Task OnlyTheProfilesOwnFolderCanBeClearedWithoutAdministratorRights()
    {
        var crashDumps = Populate(Path.Combine(_environment.LocalAppData, "CrashDumps"));
        Populate(Path.Combine(WerFolder, "ReportArchive"));
        Populate(Path.Combine(Windows, "Minidump"));

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.RequiresElevation);
        Assert.Contains(plan.Notes, n => n.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase));

        var steps = plan.Steps.OfType<DeleteStep>().ToDictionary(s => s.Path, s => s.RequiresElevation);

        Assert.False(steps[crashDumps]);
        Assert.All(steps.Where(s => s.Key != crashDumps), s => Assert.True(s.Value));

        // The offer is what turns the claim into something the user can act on, and it must fire
        // for this reason alone rather than only for a slow measurement.
        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false, [new Finding(CreateProvider(), IsPresent: true, plan)]));
    }

    /// <summary>
    /// A declared path reached by name has none of the protection an enumeration gives away, and
    /// this is the case the GPU shader caches met first: a junctioned target is enumerated through,
    /// the far side is deleted, and every survivor the plan names resolves through the link and
    /// passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedTargetIsNamedRatherThanFollowed()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), name: "irreplaceable.bin");
        var bystander = Path.Combine(outside, "irreplaceable.bin");

        Directory.CreateSymbolicLink(Path.Combine(Windows, "Minidump"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        // Nothing targeted and something declined, so the row must not read "Already clear".
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "a junctioned target was deleted through");
    }

    /// <summary>
    /// The same rule at a level the declaration only passes through. <c>ReportArchive</c> sits three
    /// directories below <c>%PROGRAMDATA%</c>, so a check on the final path alone would walk straight
    /// through a junctioned <c>Microsoft</c> and delete in a tree the plan never named.
    /// </summary>
    [Fact]
    public async Task AJunctionOnTheWayDownToANestedTargetIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var archive = Populate(Path.Combine(outside, "Windows", "WER", "ReportArchive"), name: "report.wer");
        var bystander = Path.Combine(archive, "report.wer");

        Directory.CreateSymbolicLink(Path.Combine(ProgramData, "Microsoft"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Microsoft", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "planning looked through a junctioned parent");
    }

    /// <summary>§5.6 has to fail loudly, or it is decoration.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheWindowsDirectoryVanished()
    {
        Populate(Path.Combine(Windows, "Minidump"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The over-broad rule §5.6 exists to catch: the target's parent went with it.
        Directory.Delete(Windows, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(Windows, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.3. A crash dump keeps whatever directory layout the failing application had, and a
    /// truncation here is a partial deletion of something already irreplaceable.
    ///
    /// A crash guard rather than a discriminating test: .NET prefixes long paths itself, so an
    /// outcome-based check passes even with <see cref="LongPath.Extended"/> removed.
    /// <see cref="FileRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/> and its
    /// directory counterpart are what prove the form.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var dumps = Path.Combine(_environment.LocalAppData, "CrashDumps");
        Directory.CreateDirectory(dumps);

        var deep = dumps;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var file = Path.Combine(deep, "application.dmp");
        Assert.True(file.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(file), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(dumps, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "a dump past MAX_PATH was not measured.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(file), "a file past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(dumps));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3. <c>WerFault</c> is what writes these, so the folder being in use is ordinary — and the
    /// user should be told before they type the phrase rather than afterwards.
    /// </summary>
    [Fact]
    public async Task WarnsWhileTheErrorReportingServiceIsWritingDumps()
    {
        Populate(Path.Combine(_environment.LocalAppData, "CrashDumps"));

        var provider = new CrashDumpProvider(
            _environment,
            new FakeProcessRunner(),
            new FakeProcessInspector("WerFault"),
            system: _system);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning &&
            n.Message.Contains("WerFault", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declaration itself, pinned by name rather than by shape.
    ///
    /// Asserting that a root is absent from its own targets proves nothing: a target is built by
    /// combining the root with a relative path, so only an empty relative path could make the two
    /// equal. What has to hold is that the declared set is exactly these six, since a seventh entry
    /// added without a test is a path nobody decided to delete — and that only <c>MEMORY.DMP</c> is
    /// a file, since a directory declared as a file would be measured and removed by the wrong code.
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheSixPathsAndNothingElse()
    {
        var provider = CreateProvider();

        Assert.Equal(
            [
                Path.Combine(_environment.LocalAppData, "CrashDumps"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(Windows, "LiveKernelReports"),
                Path.Combine(Windows, "Minidump"),
                Path.Combine(Windows, "MEMORY.DMP"),
            ],
            provider.Roots.SelectMany(r => r.Locations.Select(l => Path.Combine(r.Path, l.RelativePath))));

        Assert.Equal(
            ["MEMORY.DMP"],
            provider.Roots
                .SelectMany(r => r.Locations)
                .Where(l => l.Kind == DeclaredLocationKind.File)
                .Select(l => l.RelativePath));

        var windowsRoot = Assert.Single(provider.Roots, r =>
            r.Path.Equals(Windows, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(WindowsSystemRoot.Exclusions, windowsRoot.ProtectedNames);
        Assert.True(windowsRoot.RequiresElevation);

        // The profile's own folder is the one root that does not, which is what makes the
        // per-step elevation claim a real distinction rather than a constant.
        Assert.Contains(provider.Roots, r => !r.RequiresElevation);
    }

    /// <summary>
    /// A step whose whole subject is one recent file has nothing left to do, so the offer is
    /// withdrawn rather than left on the screen to reclaim nothing — and §5.6 is told to prove the
    /// file is still there afterwards.
    ///
    /// <para>The negative is the assertion that matters. That the row is gone is a display fact;
    /// that <c>MEMORY.DMP</c> is still on the disk after a run is the promise the setting made.</para>
    /// </summary>
    [Fact]
    public async Task WithdrawsTheKernelDumpWhileItIsInsideTheGuardWindowAndProvesItSurvived()
    {
        var memoryDump = Path.Combine(Windows, "MEMORY.DMP");
        File.WriteAllBytes(memoryDump, new byte[131072]);

        var plan = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.DoesNotContain(plan.Steps, s => s is DeleteFileStep);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == memoryDump && p.ExistedBefore);

        var result = await CreateProvider().ExecuteAsync(plan);

        Assert.True(File.Exists(memoryDump), "a file inside the guard window was deleted");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same dump, older than the window, is offered and removed exactly as before. A guard that
    /// held everything back would be indistinguishable from one that worked.
    /// </summary>
    [Fact]
    public async Task StillRemovesAKernelDumpOlderThanTheGuardWindow()
    {
        var memoryDump = Path.Combine(Windows, "MEMORY.DMP");
        File.WriteAllBytes(memoryDump, new byte[131072]);
        TempDirectory.Age(memoryDump, TimeSpan.FromDays(11));

        var plan = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        var file = Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps));
        Assert.Equal(131072, file.EstimatedBytes);

        var result = await CreateProvider().ExecuteAsync(plan);

        Assert.False(File.Exists(memoryDump));
        Assert.Equal(131072, result.BytesReclaimed);
    }
}
