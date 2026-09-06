using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Epic launcher's second data directory, the one outside anybody's profile.
///
/// <para>§5.2 is enforced here the way <see cref="CrashDumpProviderTests"/> enforces it rather than
/// the way <see cref="EpicLauncherWebCacheProviderTests"/> does: nothing is enumerated, so there is
/// no unrecognised child to classify, and what has to be shown instead is that a rule reaching into
/// this folder cannot reach the siblings beside it. Those siblings are unusually consequential —
/// <c>Manifests</c> is the launcher's record of which games are installed — so the negative
/// assertion is the whole of the test that matters here.</para>
/// </summary>
public sealed class EpicLauncherContentCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public EpicLauncherContentCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string EpicRoot => Path.Combine(_system.ProgramData, "Epic");

    private string LauncherRoot => Path.Combine(EpicRoot, "EpicGamesLauncher");

    private string DataFolder => Path.Combine(LauncherRoot, "Data");

    private string ContentCache => Path.Combine(DataFolder, "ContentCache");

    private EpicLauncherContentCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A directory with one file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096, string name = "artwork.jpg")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasNeverRunHere()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// <c>%PROGRAMDATA%\Epic</c> is written by the Unreal Engine launcher and by Epic Online
    /// Services as well as by the store, so the root existing must not read as presence. That would
    /// report this source on a machine that has never opened the store and then plan nothing.
    /// </summary>
    [Fact]
    public async Task TheEpicRootExistingIsNotPresence()
    {
        Populate(Path.Combine(EpicRoot, "EpicOnlineServices"), name: "sdk.dll");
        Populate(Path.Combine(DataFolder, "Manifests"), name: "installed.item");

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansTheArtworkCacheAndNothingElse()
    {
        Populate(ContentCache, bytes: 8192);
        Populate(Path.Combine(DataFolder, "Manifests"), name: "installed.item");
        Populate(Path.Combine(LauncherRoot, "VaultCache"), name: "chunk.bin");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(ContentCache, Assert.Single(plan.TargetedPaths));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(8192, plan.EstimatedBytes);
    }

    /// <summary>
    /// §3. The pictures are fetched from Epic again on demand, which is what Tier 1 asks of a
    /// candidate, so the row is ticked for the user and needs no typed phrase.
    /// </summary>
    [Fact]
    public async Task ATier1PlanIsPreSelectedAndNeedsNoTypedPhrase()
    {
        Populate(ContentCache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var finding = new Finding(provider, IsPresent: true, plan);

        Assert.True(finding.IsPreSelectedByDefault);
        Assert.NotEqual(ConfirmationLevel.TypedPhrase, ConfirmationRequirement.For(plan).Level);
    }

    /// <summary>
    /// §5.6 against the siblings this provider sits among, proved by running a plan rather than by
    /// reading the declaration.
    ///
    /// <para>An over-broad rule passes every positive assertion, so the only evidence that this one
    /// is not over-broad is that <c>Manifests</c>, <c>VaultCache</c>, <c>EMS</c> and the rest are
    /// still there afterwards. <c>Manifests</c> is the consequential one: the launcher's record of
    /// which games are installed, and losing it makes an installed library disappear from the
    /// launcher.</para>
    ///
    /// <para>§5.2's unrecognised case is here too, in the form this provider can have one. There is
    /// no classification to get wrong, so what has to hold is that a directory the table never named
    /// is never reached at all — which is what <c>Webcache</c> below is for.</para>
    /// </summary>
    [Fact]
    public async Task TheLaunchersOwnRecordsSurviveARunAndAreAssertedRatherThanMerelyOmitted()
    {
        Populate(ContentCache, bytes: 16384);

        var manifests = Populate(Path.Combine(DataFolder, "Manifests"), name: "installed.item");
        var manifestTemp = Populate(Path.Combine(DataFolder, "ManifestTemp"), name: "pending.item");
        var vault = Populate(Path.Combine(LauncherRoot, "VaultCache"), name: "chunk.bin");
        var ems = Populate(Path.Combine(DataFolder, "EMS"), name: "panel.layout");
        var downloads = Populate(Path.Combine(DataFolder, "DownloadManager"), name: "partial.state");
        var update = Populate(Path.Combine(DataFolder, "Update"), name: "pending.egu");
        var catalog = Populate(Path.Combine(DataFolder, "Catalog"), name: "catalog.json");
        var sdMeta = Populate(Path.Combine(DataFolder, "SDMeta"), name: "meta.sdmeta");
        var managed = Populate(Path.Combine(DataFolder, "ThirPartyManagedApps"), name: "app.json");

        // Outside EpicGamesLauncher entirely. The root was declared at Epic so that VaultCache could
        // be named, and these are the other products that root actually holds.
        var services = Populate(Path.Combine(EpicRoot, "EpicOnlineServices"), name: "service.exe");
        var unreal = Path.Combine(EpicRoot, "UnrealEngineLauncher", "LauncherInstalled.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(unreal)!);
        File.WriteAllBytes(unreal, new byte[128]);

        // Files rather than directories, so they are never enumerated and are asserted only because
        // the declaration names them. An assertion that Data survived would pass with both gone.
        var manifest = Path.Combine(DataFolder, "Launcher.manifest");
        var manifestMeta = Path.Combine(DataFolder, "Launcher.manifest.meta");
        File.WriteAllBytes(manifest, new byte[64]);
        File.WriteAllBytes(manifestMeta, new byte[32]);

        // A neighbour the table does not mention, and one deliberately named like something this
        // repository does clean elsewhere. It is unreachable by construction, so it carries no
        // assertion — which is why it is executed against rather than only read.
        var unnamed = Populate(Path.Combine(DataFolder, "Webcache"), name: "page.bin");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] mustSurvive =
        [
            EpicRoot, LauncherRoot, DataFolder, manifests, manifestTemp, vault, ems, downloads,
            update, catalog, sdMeta, managed, services, unnamed,
        ];

        foreach (var spared in mustSurvive)
        {
            Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }

        // Named rather than merely absent, so the run produces evidence about them.
        string[] mustBeAsserted =
        [
            EpicRoot, LauncherRoot, DataFolder, manifests, manifestTemp, vault, ems, downloads,
            update, catalog, sdMeta, managed, manifest, manifestMeta, services, unreal,
        ];

        foreach (var asserted in mustBeAsserted)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(asserted, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(ContentCache));

        foreach (var spared in mustSurvive)
        {
            Assert.True(Directory.Exists(spared), $"{Path.GetFileName(spared)} was destroyed");
        }

        Assert.True(File.Exists(manifest), "the launcher's own build record went with the cache");
        Assert.True(File.Exists(manifestMeta), "the build record's metadata went with the cache");
        Assert.True(File.Exists(unreal), "the machine's record of installed games went with the cache");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6 has to fail loudly, or it is decoration.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheLaunchersDataFolderVanished()
    {
        Populate(ContentCache);
        Populate(Path.Combine(DataFolder, "Manifests"), name: "installed.item");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The over-broad rule §5.6 exists to catch: the target's parent went with it.
        Directory.Delete(DataFolder, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(
            verification.Failures,
            c => c.Path.Equals(DataFolder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <see cref="CleanupStep.RequiresElevation"/> means the step can be seen and cannot be
    /// performed, and that is not true here — so the plan must not say it is.
    ///
    /// <para>Sitting under <c>%PROGRAMDATA%</c> is not the question. Epic's installer writes an
    /// explicit <c>BUILTIN\Users:(OI)(CI)(F)</c> onto <c>%PROGRAMDATA%\Epic</c> which inherits down
    /// to every file in the cache, so the signed-in user may remove it as they are. The shell
    /// refuses to tick a row whose step needs elevation, so declaring it would send somebody
    /// through a relaunch to reclaim half a gigabyte they could already reclaim, and would put a
    /// note in front of them that their own disk contradicts.</para>
    ///
    /// <para>The note is the assertion that matters, and it has to name the sentence rather than the
    /// word. <see cref="DeclaredLocations"/> adds "can remove them only while it is running as
    /// administrator" when a target declares the flag, and <see cref="ScanStrategy"/> separately
    /// says "Running Deguffer as administrator lets it read the volume's file table" whenever the
    /// size was walked for — which happens on every unelevated run of this suite. A test matching
    /// the bare word would pass with the flag left on.</para>
    ///
    /// <para>What Core cannot show is the consequence: the shell's refusal to tick such a row lives
    /// in <c>StepViewModel</c>, in a project with no tests. So this asserts the claim, and the
    /// claim is what that refusal reads.</para>
    /// </summary>
    [Fact]
    public async Task ClaimsNoAdministratorRightsItDoesNotNeed()
    {
        Populate(ContentCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.False(plan.RequiresElevation);
        Assert.False(Assert.Single(plan.Steps).RequiresElevation);

        Assert.DoesNotContain(
            plan.Notes,
            n => n.Message.Contains("only while it is running as administrator", StringComparison.Ordinal));
    }

    /// <summary>
    /// A declared path reached by name has none of the protection an enumeration gives away. A
    /// junctioned cache is enumerated through, the far side is deleted, and every survivor the plan
    /// names resolves through the link and passes — the vacuous negative.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsNamedRatherThanFollowed()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), name: "irreplaceable.bin");
        var bystander = Path.Combine(outside, "irreplaceable.bin");

        Directory.CreateDirectory(DataFolder);
        Directory.CreateSymbolicLink(ContentCache, outside);

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
    /// The same rule at a level the declaration only passes through. The cache sits three
    /// directories below <c>%PROGRAMDATA%</c>, and moving the launcher's machine-wide folder onto
    /// another drive with a junction is a thing people do — after which a check on the final path
    /// alone would delete in a tree the plan never named.
    /// </summary>
    [Fact]
    public async Task AJunctionOnTheWayDownToTheCacheIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var cache = Populate(Path.Combine(outside, "Data", "ContentCache"));
        var bystander = Path.Combine(cache, "artwork.jpg");

        Directory.CreateDirectory(EpicRoot);
        Directory.CreateSymbolicLink(LauncherRoot, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("EpicGamesLauncher", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "planning looked through a junctioned parent");
    }

    /// <summary>
    /// §5.3. The launcher writes artwork into this folder as the store is browsed, so the folder
    /// being in use is ordinary — and the user should be told before they clean rather than
    /// afterwards.
    /// </summary>
    [Fact]
    public async Task WarnsWhileTheLauncherIsRunning()
    {
        Populate(ContentCache);

        var provider = new EpicLauncherContentCacheProvider(
            _environment,
            new FakeProcessRunner(),
            new FakeProcessInspector("EpicGamesLauncher"),
            system: _system);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning &&
            n.Message.Contains("EpicGamesLauncher", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declaration itself, pinned by name rather than by shape.
    ///
    /// Asserting that the root is absent from its own targets proves nothing: a target is the root
    /// combined with a relative path, so only an empty relative path could make the two equal. What
    /// has to hold is that the declared set is exactly this one path — a second entry added without
    /// a test is a directory nobody decided to delete — and that the siblings §5.6 reports are the
    /// ones somebody actually chose.
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheOnePathAndNothingElse()
    {
        var provider = CreateProvider();
        var root = Assert.Single(provider.Roots);

        Assert.Equal(EpicRoot, root.Path);
        Assert.False(root.RequiresElevation);

        Assert.Equal(
            ContentCache,
            Path.Combine(root.Path, Assert.Single(root.Locations).RelativePath));

        Assert.All(root.Locations, l => Assert.Equal(DeclaredLocationKind.Directory, l.Kind));

        Assert.Equal(
            [
                @"EpicGamesLauncher\Data\Manifests",
                @"EpicGamesLauncher\Data\ManifestTemp",
                @"EpicGamesLauncher\VaultCache",
                @"EpicGamesLauncher\Data\DownloadManager",
                @"EpicGamesLauncher\Data\Update",
                @"EpicGamesLauncher\Data\EMS",
                @"EpicGamesLauncher\Data\Catalog",
                @"EpicGamesLauncher\Data\SDMeta",
                @"EpicGamesLauncher\Data\ThirPartyManagedApps",
                @"EpicGamesLauncher\Data\Launcher.manifest",
                @"EpicGamesLauncher\Data\Launcher.manifest.meta",
                @"UnrealEngineLauncher\LauncherInstalled.dat",
                "EpicOnlineServices",
            ],
            root.ProtectedNames.Select(p => p.RelativePath));
    }

    /// <summary>
    /// §7's age column, left blank rather than filled in with the last store page somebody opened.
    ///
    /// <para><see cref="CleanupStep.LastWritten"/> says a whole-cache step leaves this null, because
    /// one timestamp across everything a tool ever cached is a number with nothing to mean. The
    /// measured folder ran across four years, so a date here would say "today" about a cache that is
    /// mostly ancient — and §7 renders an age as an invitation to delete.</para>
    /// </summary>
    [Fact]
    public async Task ReportsNoAgeForACacheWhoseOneDateWouldMeanNothing()
    {
        Populate(ContentCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.Null(Assert.Single(plan.Steps).LastWritten);
    }

    /// <summary>
    /// §6.3. The cache is flat on the measured machine, but the launcher decides that and not
    /// Deguffer, and a <c>MAX_PATH</c> truncation here is a silent partial deletion.
    ///
    /// A crash guard rather than a discriminating test: .NET prefixes long paths itself, so an
    /// outcome-based check passes even with <see cref="LongPath.Extended"/> removed. This provider
    /// declares one directory and no file, so what proves Core applies the prefix on the path this
    /// row removes is
    /// <see cref="DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/>.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        Directory.CreateDirectory(ContentCache);

        var deep = ContentCache;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var file = Path.Combine(deep, "artwork.jpg");
        Assert.True(file.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(file), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(ContentCache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "artwork past MAX_PATH was not measured.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(file), "a file past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(ContentCache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The user's guard on recently touched files, which matters more here than on most rows: the
    /// artwork on the page somebody is looking at right now was written seconds ago.
    ///
    /// <para>The row stays and measures nothing, rather than disappearing. That is the distinction
    /// <see cref="CleanupPlan.HasRecentContentHeldBack"/> exists for — a zero on a location that is
    /// full is a different fact from a zero on one that is empty, and the shell says so.</para>
    /// </summary>
    [Fact]
    public async Task HoldsBackArtworkWrittenInsideTheGuardWindow()
    {
        var recent = Path.Combine(Populate(ContentCache), "artwork.jpg");

        var plan = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Equal(0, plan.EstimatedBytes);

        var result = await CreateProvider().ExecuteAsync(plan);

        Assert.True(File.Exists(recent), "artwork inside the guard window was deleted");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same cache, older than the window, is offered and removed exactly as before. A guard that
    /// held everything back would be indistinguishable from one that worked.
    /// </summary>
    [Fact]
    public async Task StillRemovesArtworkOlderThanTheGuardWindow()
    {
        var old = Path.Combine(Populate(ContentCache), "artwork.jpg");
        TempDirectory.Age(old, TimeSpan.FromDays(30));

        var plan = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.False(plan.HasRecentContentHeldBack);
        Assert.Equal(4096, plan.EstimatedBytes);

        var result = await CreateProvider().ExecuteAsync(plan);

        Assert.False(File.Exists(old));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
