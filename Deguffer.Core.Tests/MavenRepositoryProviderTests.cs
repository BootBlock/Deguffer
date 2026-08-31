using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Maven names the one directory it removes rather than listing the root that holds it, so §5.2 is
/// enforced the way the declared-path providers enforce it: there is no enumeration through which
/// an unnamed sibling could be reached at all. What that has to be shown to mean is that the
/// credentials in <c>settings.xml</c>, the master password beside it and anything else in the root
/// are all still there after a run — which
/// <see cref="ExecutingRemovesTheRepositoryAndLeavesTheRestOfTheRootStanding"/> executes a plan to
/// establish.
/// </summary>
public sealed class MavenRepositoryProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public MavenRepositoryProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private MavenRepositoryProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Home => Path.Combine(_environment.UserProfile, ".m2");

    private string DefaultRepository => Path.Combine(Home, "repository");

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "artefact.jar"), new byte[bytes]);
        return directory;
    }

    /// <summary>A settings file, in Maven's own namespace, optionally naming a local repository.</summary>
    private string WriteSettings(string? localRepository = null)
    {
        var element = localRepository is null
            ? string.Empty
            : $"  <localRepository>{localRepository}</localRepository>\n";

        var settings = Path.Combine(Home, "settings.xml");
        Directory.CreateDirectory(Home);
        File.WriteAllText(
            settings,
            "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\">\n"
            + element
            + "  <servers><server><id>internal</id><username>ci</username></server></servers>\n"
            + "</settings>\n");

        return settings;
    }

    [Fact]
    public async Task ReportsNotPresentWhenMavenHasNeverRun()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// A settings file with no repository beside it is a configured machine that has never resolved
    /// anything. Reading the root as presence would offer a row the plan has nothing to say about.
    /// </summary>
    [Fact]
    public async Task AConfiguredRootWithNoRepositoryIsNotPresence()
    {
        WriteSettings();

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansTheRepositoryAndNeverTheRootThatHoldsIt()
    {
        Populate(DefaultRepository);
        WriteSettings();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([DefaultRepository], plan.TargetedPaths);
        Assert.DoesNotContain(Home, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Home, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// The §5.2 trap the survey names verbatim. Both files hold credentials, both sit in the root,
    /// and both are asserted to survive rather than merely being left out of the plan.
    /// </summary>
    [Fact]
    public async Task NeverPlansTheServerCredentialsOrTheMasterPassword()
    {
        Populate(DefaultRepository);
        var settings = WriteSettings();
        var security = Path.Combine(Home, "settings-security.xml");
        File.WriteAllText(security, "<settingsSecurity><master>{redacted}</master></settingsSecurity>");

        var plan = await CreateProvider().PlanAsync();

        foreach (var file in (string[])[settings, security])
        {
            Assert.DoesNotContain(file, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path => Assert.False(
                IsAtOrUnder(file, path), $"{path} would have taken {Path.GetFileName(file)} with it."));

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(file, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    [Fact]
    public async Task HonoursALocalRepositoryConfiguredInSettingsXml()
    {
        var moved = Populate(Path.Combine(_temp.Path, "shared", "m2-repository"));
        WriteSettings(moved);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);
        Assert.DoesNotContain(DefaultRepository, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one property Maven interpolation idiom this resolves, because writing the settings file
    /// portably is the common reason to use it at all.
    /// </summary>
    [Fact]
    public async Task ResolvesTheUserHomePropertyInAConfiguredPath()
    {
        var moved = Populate(Path.Combine(_environment.UserProfile, "m2-repository"));
        WriteSettings("${user.home}/m2-repository");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);
    }

    /// <summary>
    /// §5.2. A configured value naming the Maven home, or anything above it, would make the tool
    /// root the target — and the same plan would delete <c>.m2</c> while asserting that the
    /// <c>settings.xml</c> inside it survives. Both of these are a plausible typo for the correct
    /// <c>${user.home}/.m2/repository</c>, and a settings file arrives from a dotfiles repository as
    /// often as it is typed.
    /// </summary>
    [Theory]
    [InlineData("${user.home}")]
    [InlineData("${user.home}/.m2")]
    public async Task RefusesALocalRepositoryThatWouldTakeTheMavenHomeWithIt(string configured)
    {
        Populate(DefaultRepository);
        WriteSettings(configured);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("holds your Maven configuration", StringComparison.Ordinal));
    }

    /// <summary>
    /// One level in from the guard above, and the same contradiction: the plan would target the
    /// directory it also names as a survivor, and §5.6 would report a correct run as a failure.
    /// </summary>
    [Fact]
    public async Task RefusesALocalRepositoryThatNamesSomethingItPromisesToLeaveAlone()
    {
        Populate(DefaultRepository);
        Populate(Path.Combine(Home, "wrapper", "dists"));
        WriteSettings("${user.home}/.m2/wrapper");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("'wrapper'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The device-namespace form of the same value. It is fully qualified, so it reaches the guard,
    /// and it would compare equal to nothing unless the prefix comes off first.
    /// </summary>
    [Fact]
    public async Task RefusesTheMavenHomeWrittenInTheDeviceNamespace()
    {
        Populate(DefaultRepository);
        WriteSettings(@"\\?\" + Home);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// A trailing separator makes the leaf name empty, and a location with an empty relative path
    /// resolves back to the root that holds it — so the plan would target the very directory it also
    /// asserts must survive, and §5.6 would report a correct run as a failure.
    /// </summary>
    [Fact]
    public async Task NormalisesAConfiguredRepositoryThatEndsInASeparator()
    {
        var moved = Populate(Path.Combine(_temp.Path, "shared", "m2-repository"));
        WriteSettings(moved + Path.DirectorySeparatorChar);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p =>
            p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A relative or otherwise unresolvable value names a directory Deguffer cannot place, and
    /// reaching into one nobody pointed at is the guess §5.2 forbids.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheConfiguredRepositoryIsNotAFullPath()
    {
        Populate(DefaultRepository);
        WriteSettings("${settings.localRepository}/artifacts");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// Even with the repository moved away, the credentials are still in <c>.m2</c> and are still
    /// what §5.6 has to have subjects for. That is the second declared root, which names no location
    /// at all.
    /// </summary>
    [Fact]
    public async Task StillAssertsTheCredentialsWhenTheRepositoryHasMovedElsewhere()
    {
        var moved = Populate(Path.Combine(_temp.Path, "shared", "m2-repository"));
        var settings = WriteSettings(moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(settings, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Home, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// §7's age column is deliberately blank here. A repository nests by group, artifact and version
    /// before it reaches a file, so its top level moves only when a whole new group first appears —
    /// and a repository built against every day would report as years old, which is backwards for
    /// the one thing an age is read for.
    /// </summary>
    [Fact]
    public async Task ReportsNoAgeForARepositoryBecauseItsTopLevelDoesNotMoveWithUse()
    {
        Populate(DefaultRepository);

        var plan = await CreateProvider().PlanAsync();

        Assert.Null(Assert.Single(plan.Steps).LastWritten);
    }

    [Fact]
    public async Task DeclinesARepositoryThatIsALinkToSomewhereElse()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        Directory.CreateDirectory(Home);
        Directory.CreateSymbolicLink(DefaultRepository, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.True(Directory.Exists(outside));
    }

    /// <summary>
    /// §6.3. A Maven coordinate becomes a directory per group segment, so a deeply nested group with
    /// a long artefact name passes <c>MAX_PATH</c> without trying, and a truncation there is a
    /// silent partial deletion.
    /// </summary>
    [Fact]
    public async Task ReachesAnArtefactBeyondMaxPath()
    {
        Directory.CreateDirectory(DefaultRepository);

        var deep = DefaultRepository;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('g', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "artefact.jar")), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(8192, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(8192, result.BytesReclaimed);
        Assert.False(Directory.Exists(LongPath.Extended(deep)));
    }

    /// <summary>
    /// §5.6, executed rather than asserted on paper. The unnamed neighbour matters as much as the
    /// named ones: a rule that removed the root would take a directory nothing in this provider ever
    /// mentions, and only running it shows that it does not.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheRepositoryAndLeavesTheRestOfTheRootStanding()
    {
        Populate(DefaultRepository);
        var settings = WriteSettings();
        var wrapper = Populate(Path.Combine(Home, "wrapper", "dists"));
        var stray = Populate(Path.Combine(Home, "something-else"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(DefaultRepository));

        Assert.True(Directory.Exists(Home));
        Assert.True(File.Exists(settings));
        Assert.True(Directory.Exists(wrapper));
        Assert.True(Directory.Exists(stray));

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheSettingsFileVanished()
    {
        Populate(DefaultRepository);
        var settings = WriteSettings();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        File.Delete(settings);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(settings, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The settings file is edited by hand and by every IDE, so a rescan has to read it again rather
    /// than measure a directory Maven has stopped filling.
    /// </summary>
    [Fact]
    public async Task ReadsTheSettingsFileAgainAfterAnInvalidation()
    {
        Populate(DefaultRepository);
        var moved = Populate(Path.Combine(_temp.Path, "shared", "m2-repository"));

        var provider = CreateProvider();
        Assert.Equal([DefaultRepository], (await provider.PlanAsync()).TargetedPaths);

        WriteSettings(moved);
        provider.InvalidateCaches();

        Assert.Equal([moved], (await provider.PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task WarnsWhenAJavaProcessIsHoldingTheRepositoryOpen()
    {
        Populate(DefaultRepository);

        var provider = new MavenRepositoryProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("java"));

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
