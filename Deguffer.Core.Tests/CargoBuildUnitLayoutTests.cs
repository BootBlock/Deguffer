using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The profile layout Cargo writes from 1.100, which keeps each build unit's fingerprints under
/// <c>build\&lt;package&gt;\&lt;hash&gt;</c> and no <c>.fingerprint</c> in the profile, read from Cargo's
/// own <see href="https://github.com/rust-lang/cargo/blob/master/src/compiler/layout.rs">layout.rs</see>.
/// No Rust toolchain runs for any of it.
/// </summary>
public sealed class CargoBuildUnitLayoutTests : IDisposable
{
    private readonly CargoTargetFixture _cargo = new();

    public void Dispose() => _cargo.Dispose();

    /// <summary>
    /// A profile Cargo 1.100 built is offered on the build output row emptied around its
    /// <c>incremental</c>, which the incremental row offers. A build script's folder beside it is named,
    /// and survives both rows, and §5.6 says so.
    /// </summary>
    [Fact]
    public async Task AProfileInTheBuildUnitLayoutIsOfferedByBothRows()
    {
        var target = _cargo.CargoProject("app");
        var profile = BuildDirectoryFixture.CreateCargoBuildUnitProfile(target, "release", writeIncremental: true);
        var incremental = Path.Combine(profile, "incremental");
        var archives = Directory.CreateDirectory(Path.Combine(target, "dist-check")).FullName;
        File.WriteAllText(Path.Combine(archives, "app-1.0.0-x64.zip"), "release");

        var output = _cargo.BuildOutput();
        var outputPlan = await output.PlanAsync();
        var cache = _cargo.Incremental();
        var incrementalPlan = await cache.PlanAsync();

        var profileStep = Assert.Single(outputPlan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == profile);
        Assert.Equal([incremental], profileStep.OwnedElsewhere);
        Assert.Contains(outputPlan.Steps, s => s.Description.StartsWith("Rust build output (release) for app", StringComparison.Ordinal));
        Assert.Equal([incremental], incrementalPlan.TargetedPaths);
        Assert.Contains(outputPlan.ProtectedPaths, p => p.Path == archives && p.PresenceBefore is PathPresence.Present);

        var reach = RunReach.Of([outputPlan, incrementalPlan]);
        var residue = new RunResidue();

        foreach (var (provider, plan) in new (ICleanupProvider, CleanupPlan)[] { (output, outputPlan), (cache, incrementalPlan) })
        {
            var result = await provider.ExecuteAsync(plan, reach, residue);
            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(Directory.Exists(Path.Combine(profile, "build")));
        Assert.False(File.Exists(Path.Combine(profile, "example.exe")));
        Assert.False(Directory.Exists(incremental));
        Assert.True(File.Exists(Path.Combine(archives, "app-1.0.0-x64.zip")));
        Assert.True(File.Exists(Path.Combine(target, "CACHEDIR.TAG")));
    }

    /// <summary>
    /// A profile an older and a newer Cargo both built holds both layouts side by side. All of it is
    /// Cargo's, so it is one profile, offered once, and nothing inside it is named as left alone.
    /// </summary>
    [Fact]
    public async Task AProfileHoldingBothLayoutsIsOfferedOnce()
    {
        var target = _cargo.CargoProject("app");
        var profile = BuildDirectoryFixture.CreateCargoProfile(target, "release");
        BuildDirectoryFixture.CreateCargoBuildUnitProfile(target, "release");

        var plan = await _cargo.BuildOutput().PlanAsync();

        Assert.Equal(
            [Path.Combine(target, "debug"), profile],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build units without the lock Cargo takes on every profile are somebody else's folder, however
    /// Cargo-like they look.
    /// </summary>
    [Fact]
    public async Task BuildUnitsWithoutCargosLockAreLeftAlone()
    {
        var target = _cargo.CargoProject("app");
        var lookalike = Directory.CreateDirectory(Path.Combine(target, "bench")).FullName;
        Directory.CreateDirectory(Path.Combine(lookalike, "build", "x", "y", "fingerprint"));

        var plan = await _cargo.BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == lookalike && p.Reason.StartsWith("Not something Cargo", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>build</c> whose first unit holds no fingerprints is not Cargo's build units, so the lock
    /// beside it is all the evidence there is.
    /// </summary>
    [Fact]
    public async Task ABuildFolderWithoutFingerprintsIsNotEvidence()
    {
        var target = _cargo.CargoProject("app");
        var lookalike = Directory.CreateDirectory(Path.Combine(target, "release")).FullName;
        File.WriteAllText(Path.Combine(lookalike, ".cargo-lock"), string.Empty);
        Directory.CreateDirectory(Path.Combine(lookalike, "build", "x", "y", "out"));

        var plan = await _cargo.BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == lookalike);
    }

    /// <summary>
    /// A build unit reached through a link is not read as evidence: what the link points at is not
    /// inside the profile.
    /// </summary>
    [Fact]
    public async Task ABuildUnitBehindALinkIsNotEvidence()
    {
        var target = _cargo.CargoProject("app");
        var lookalike = Directory.CreateDirectory(Path.Combine(target, "release")).FullName;
        File.WriteAllText(Path.Combine(lookalike, ".cargo-lock"), string.Empty);
        var package = Directory.CreateDirectory(Path.Combine(lookalike, "build", "example")).FullName;
        var elsewhere = _cargo.Temp.CreateDirectory("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "fingerprint"));
        Junction.ToDirectory(Path.Combine(package, "0123abcd4567ef89"), elsewhere);

        var plan = await _cargo.BuildOutput().PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == lookalike);
    }

    /// <summary>
    /// The folder of finished programs Cargo leaves in <c>target</c> when its build folder is moved
    /// elsewhere holds only the lock, so it is left alone, with a reason that says what was missing.
    /// </summary>
    [Fact]
    public async Task AProfileHoldingOnlyCargosLockIsLeftAloneAndSaysWhy()
    {
        var target = _cargo.CargoProject("app");
        var artefacts = Directory.CreateDirectory(Path.Combine(target, "release")).FullName;
        File.WriteAllText(Path.Combine(artefacts, ".cargo-lock"), string.Empty);
        File.WriteAllText(Path.Combine(artefacts, "app.exe"), "program");
        Directory.CreateDirectory(Path.Combine(artefacts, "examples"));

        var provider = _cargo.BuildOutput();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(target, "debug")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == artefacts && p.Reason.StartsWith("Holds the lock Cargo takes", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(Path.Combine(artefacts, "app.exe")));
    }
}
