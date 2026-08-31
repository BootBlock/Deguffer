using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Cargo is the first provider to classify children at three levels of one root, so §5.2 has to
/// hold at every one of them. These are mostly negative tests: the Cargo home holds registry
/// credentials, the user's configuration and every binary <c>cargo install</c> ever put on their
/// <c>PATH</c>, so what must never appear in a plan matters more than what does.
///
/// Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>. No Rust
/// toolchain is installed on the machine these were written on, which is the point rather than a
/// limitation: a rule that could only be proved where Cargo is present would not be a rule.
/// </summary>
public sealed class CargoCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public CargoCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private CargoCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Home => Path.Combine(_environment.UserProfile, ".cargo");

    /// <summary>A directory holding one file, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    private string CreateFullHome()
    {
        Populate(Path.Combine(Home, "registry", "cache"));
        Populate(Path.Combine(Home, "registry", "src"));
        Populate(Path.Combine(Home, "registry", "index"));
        Populate(Path.Combine(Home, "git", "checkouts"));
        Populate(Path.Combine(Home, "git", "db"));
        Populate(Path.Combine(Home, "bin"));

        File.WriteAllText(Path.Combine(Home, "credentials.toml"), "[registry]\ntoken = \"redacted\"\n");
        File.WriteAllText(Path.Combine(Home, "config.toml"), "[net]\ngit-fetch-with-cli = true\n");

        return Home;
    }

    [Fact]
    public async Task ReportsNotPresentWhenCargoWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// rustup creates the home with nothing in it but <c>bin</c>, so the home existing must not read
    /// as presence — that would offer a row the plan then has nothing to say about. The same rule
    /// the shader caches needed for a vendor directory.
    /// </summary>
    [Fact]
    public async Task AHomeHoldingOnlyInstalledBinariesIsNotPresence()
    {
        Populate(Path.Combine(Home, "bin"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansTheArchivesTheirUnpackedSourcesAndTheGitCheckouts()
    {
        CreateFullHome();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            [
                Path.Combine(Home, "git", "checkouts"),
                Path.Combine(Home, "registry", "cache"),
                Path.Combine(Home, "registry", "src"),
            ],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.2. Three directories are enumerated here, and not one of them may be a target: the home
    /// holds the credentials, and <c>registry</c> and <c>git</c> each hold something that stays
    /// beside something that goes.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheHomeOrEitherContainingDirectory()
    {
        CreateFullHome();

        var plan = await CreateProvider().PlanAsync();

        foreach (var directory in (string[])[Home, Path.Combine(Home, "registry"), Path.Combine(Home, "git")])
        {
            Assert.DoesNotContain(directory, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path => Assert.False(
                IsAtOrUnder(directory, path),
                $"{path} would have taken {directory} with it."));
        }
    }

    /// <summary>
    /// The §5.2 trap the survey names. A child classification only ever sees directories, so both
    /// files are invisible to it and are asserted by name — the lesson NVIDIA's <c>accounts</c>
    /// taught, in a directory with a registry token in it.
    /// </summary>
    [Fact]
    public async Task NeverPlansTheRegistryTokenOrTheUserConfiguration()
    {
        CreateFullHome();

        var plan = await CreateProvider().PlanAsync();

        foreach (var name in (string[])["credentials.toml", "config.toml"])
        {
            var file = Path.Combine(Home, name);

            Assert.DoesNotContain(file, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path => Assert.False(
                IsAtOrUnder(file, path), $"{path} would have taken {name} with it."));

            // Not merely absent from the plan — asserted to survive (§5.6).
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(file, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// §5.2's dangerous direction: a child nobody declared must land in Tier 4 rather than being
    /// treated as safe. Asserted at two levels, because a level that classified nothing would still
    /// pass a test that only looked at the root.
    /// </summary>
    [Theory]
    [InlineData("", "telemetry")]
    [InlineData("registry", "future-thing")]
    [InlineData("git", "future-thing")]
    public async Task AnUndeclaredChildIsTier4AndLeftAlone(string container, string name)
    {
        CreateFullHome();
        var unknown = Populate(Path.Combine(Home, container, name));

        var level = CargoCacheProvider.Levels.Single(l => l.ContainerName == container);
        Assert.Equal(SafetyTier.DoNotTouch, level.Children.Classify(name).Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unknown, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The two the survey listed as disposable and this provider does not target. They are declared
    /// rather than merely unrecognised, so the note the user reads says why they stay instead of
    /// saying nothing was recognised.
    /// </summary>
    [Theory]
    [InlineData("registry", "index")]
    [InlineData("git", "db")]
    public async Task TheOriginalsTheTargetsAreDerivedFromAreDeclaredAndLeftAlone(string container, string name)
    {
        CreateFullHome();
        var kept = Path.Combine(Home, container, name);

        var level = CargoCacheProvider.Levels.Single(l => l.ContainerName == container);
        var classification = level.Children.Classify(name);

        Assert.Equal(SafetyTier.DoNotTouch, classification.Tier);
        Assert.DoesNotContain("Not a recognised", classification.Reason, StringComparison.Ordinal);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(kept, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(classification.Reason, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every child this provider offers has to be the tier the provider claims, or the plan's single
    /// tier would be a claim about something it does not cover.
    /// </summary>
    [Fact]
    public void EveryDeclaredDisposableChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var level in CargoCacheProvider.Levels)
        {
            foreach (var name in level.Children.DisposableNames)
            {
                Assert.Equal(provider.Tier, level.Children.Classify(name).Tier);
            }
        }
    }

    [Fact]
    public async Task LeavesALinkedChildAloneAndSaysSo()
    {
        CreateFullHome();

        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(Home, "registry", "linked");
        Directory.CreateSymbolicLink(link, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("linked", StringComparison.Ordinal));
    }

    /// <summary>
    /// A junctioned container hands back the far side's ordinary directories, and a recognised name
    /// among them would be targeted while every survivor named for this home resolves through the
    /// link and passes — §5.6's negative made vacuous. So the level itself is checked, not only its
    /// children.
    /// </summary>
    [Fact]
    public async Task DeclinesAContainerThatIsItselfALink()
    {
        Populate(Path.Combine(Home, "registry", "cache"));

        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Populate(Path.Combine(outside, "checkouts"));
        Directory.CreateSymbolicLink(Path.Combine(Home, "git"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([Path.Combine(Home, "registry", "cache")], plan.TargetedPaths);
        Assert.Equal(
            1,
            plan.Notes.Count(n => n.Message.StartsWith("Leaving 'git' alone: A link", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HonoursCargoHomeWhenItIsSetToAFullPath()
    {
        var moved = Path.Combine(_temp.Path, "elsewhere", ".cargo");
        Populate(Path.Combine(moved, "registry", "cache"));
        _environment.WithEnvironmentVariable(CargoCacheProvider.HomeVariable, moved);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "registry", "cache")], plan.TargetedPaths);
    }

    /// <summary>
    /// Cargo resolves a relative value against the invoking shell's working directory, which
    /// Deguffer is not. There is no correct interpretation available, so nothing is offered.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenCargoHomeIsRelative()
    {
        Populate(Path.Combine(Home, "registry", "cache"));
        _environment.WithEnvironmentVariable(CargoCacheProvider.HomeVariable, @"..\shared\.cargo");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenCargoIsHoldingTheCacheOpen()
    {
        Populate(Path.Combine(Home, "registry", "cache"));

        var provider = new CargoCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("cargo"));

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// §6.3. A crate unpacked into <c>registry\src</c> nests deeply enough to pass <c>MAX_PATH</c>
    /// on its own, and a truncation there is a silent partial deletion.
    /// </summary>
    [Fact]
    public async Task ReachesACrateSourceTreeBeyondMaxPath()
    {
        var source = Path.Combine(Home, "registry", "src");
        Directory.CreateDirectory(source);

        var deep = source;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('c', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "lib.rs")), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(8192, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(8192, result.BytesReclaimed);
        Assert.False(Directory.Exists(LongPath.Extended(deep)));
    }

    /// <summary>
    /// §5.6, executed rather than asserted on paper. What the run has to establish is not only that
    /// the caches went, but that the credentials, the configuration, the installed binaries and both
    /// of the originals the targets were derived from are all still there afterwards.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesEverythingElseStanding()
    {
        CreateFullHome();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);

        Assert.False(Directory.Exists(Path.Combine(Home, "registry", "cache")));
        Assert.False(Directory.Exists(Path.Combine(Home, "registry", "src")));
        Assert.False(Directory.Exists(Path.Combine(Home, "git", "checkouts")));

        Assert.True(Directory.Exists(Home));
        Assert.True(Directory.Exists(Path.Combine(Home, "registry")));
        Assert.True(Directory.Exists(Path.Combine(Home, "registry", "index")));
        Assert.True(Directory.Exists(Path.Combine(Home, "git")));
        Assert.True(Directory.Exists(Path.Combine(Home, "git", "db")));
        Assert.True(Directory.Exists(Path.Combine(Home, "bin")));
        Assert.True(File.Exists(Path.Combine(Home, "credentials.toml")));
        Assert.True(File.Exists(Path.Combine(Home, "config.toml")));

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheRegistryTokenVanished()
    {
        CreateFullHome();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        var credentials = Path.Combine(Home, "credentials.toml");
        File.Delete(credentials);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(credentials, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
