using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shader-cache provider spans four locations under one profile, so §5.2 has to hold four times
/// over rather than once. These are mostly negative tests: <c>%LOCALAPPDATA%\NVIDIA</c> holds
/// sign-in state beside the caches, and <c>%LOCALAPPDATA%</c> is the parent of the one target that
/// is a whole directory.
/// </summary>
public sealed class GpuShaderCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public GpuShaderCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private GpuShaderCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Create <paramref name="relative"/> under the fake profile, holding one file.</summary>
    private string CreateCache(string relative, int bytes = 4096)
    {
        var directory = Path.Combine(_environment.LocalAppData, relative);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "pipeline.bin"), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoDriverHasWrittenACache()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The lesson from the Unreal cache in <c>docs/todo/unreached-locations.md</c> §8, applied before
    /// it could bite: a vendor directory exists on machines with no graphics cache in it at all, so
    /// existence of the root proves nothing.
    /// </summary>
    [Fact]
    public async Task AVendorDirectoryHoldingNoCacheIsNotPresence()
    {
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Intel", "IntelGraphicsSoftware"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansEveryVendorCacheAndTheDirect3DOneTogether()
    {
        var dxCache = CreateCache(Path.Combine("NVIDIA", "DXCache"), 8192);
        var glCache = CreateCache(Path.Combine("NVIDIA", "GLCache"), 2048);
        var amd = CreateCache(Path.Combine("AMD", "DxCache"));
        var intel = CreateCache(Path.Combine("Intel", "ShaderCache"));
        var direct3D = CreateCache("D3DSCache");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            [amd, direct3D, intel, dxCache, glCache],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
    }

    /// <summary>
    /// §7 scopes the age column to per-workspace and per-project data. A shader cache is one blob
    /// store per driver, so a timestamp on it would be a number with nothing to mean — and a number
    /// the user might act on.
    /// </summary>
    [Fact]
    public async Task NoStepCarriesAnAgeBecauseTheseAreWholeCaches()
    {
        CreateCache(Path.Combine("NVIDIA", "DXCache"));
        CreateCache("D3DSCache");

        var plan = await CreateProvider().PlanAsync();

        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
    }

    [Fact]
    public async Task NeverTargetsAVendorRootDirectory()
    {
        CreateCache(Path.Combine("NVIDIA", "DXCache"));
        CreateCache(Path.Combine("Intel", "ShaderCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var root in provider.RootPaths)
        {
            Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path =>
                Assert.False(IsAtOrUnder(root, path), $"{path} would have taken the {root} root with it."));
        }
    }

    /// <summary>
    /// The Direct3D cache is the one whole-directory target, so the only §5.6 assertion available
    /// for it is that its parent — the profile's local application data — is not what gets removed.
    /// </summary>
    [Fact]
    public async Task NeverTargetsLocalAppDataItself()
    {
        CreateCache("D3DSCache");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(_environment.LocalAppData, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(_environment.LocalAppData, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// The Direct3D cache is reached by name, so it is the one target no enumeration filtered. A
    /// junction there is the §5.2 failure in its worst form: the plan names a path inside the
    /// profile and the deletion lands wherever the link points, which the §5.6 negative — written
    /// against paths inside the profile — cannot detect. Redirecting a shader cache to another
    /// volume this way is common.
    /// </summary>
    [Fact]
    public async Task AJunctionedDirect3DCacheIsNeverATargetAndIsNotDeletedThrough()
    {
        var outside = Path.Combine(_temp.Path, "precious");
        Directory.CreateDirectory(outside);
        var bystander = Path.Combine(outside, "irreplaceable.bin");
        File.WriteAllBytes(bystander, new byte[4096]);

        var link = Path.Combine(_environment.LocalAppData, "D3DSCache");
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("D3DSCache", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(outside), "planning followed a link out of the profile");
        Assert.True(File.Exists(bystander), "a file outside the profile was destroyed");
    }

    /// <summary>
    /// §5.6 for the whole-directory target, against a real neighbour rather than only against the
    /// profile directory. An over-broad rule that took the parent's contents leaves the parent
    /// standing, so the parent alone is close to unfalsifiable; a sibling is not.
    /// </summary>
    [Fact]
    public async Task ASiblingOfTheDirect3DCacheSurvivesTheRun()
    {
        CreateCache("D3DSCache");
        var neighbour = CreateCache("SomeOtherApplication");

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(_environment.LocalAppData, "D3DSCache")));
        Assert.True(Directory.Exists(neighbour), "a neighbour of the Direct3D cache was removed with it.");
    }

    /// <summary>
    /// The table is designed to grow, and a child declared above Tier 1 would be planned under this
    /// provider's Tier 1 sentence and pre-selected, because a plan carries the provider's tier
    /// rather than the child's. <see cref="DisposableChildSet"/> offers Tier 2 and Tier 3 names, so
    /// nothing below this test would catch it.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var root in GpuShaderCacheProvider.Roots)
        {
            foreach (var name in root.Children.DisposableNames)
            {
                Assert.Equal(provider.Tier, root.Children.Classify(name).Tier);
            }
        }
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown child treated as safe, so a directory the table does
    /// not name must land in Tier 4 and be asserted to survive rather than merely omitted.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedNvidiaChildIsTier4AndSurvives()
    {
        CreateCache(Path.Combine("NVIDIA", "DXCache"));
        var sibling = CreateCache(Path.Combine("NVIDIA", "Telemetry"));

        var nvidia = GpuShaderCacheProvider.Roots.Single(r => r.DirectoryName == "NVIDIA");
        Assert.Equal(SafetyTier.DoNotTouch, nvidia.Children.Classify("Telemetry").Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("NVIDIA\\Telemetry", StringComparison.Ordinal));

        // Not merely absent from the plan — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), "Telemetry was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>accounts</c> was observed on a real machine as a <em>file</em>, which a
    /// <see cref="DisposableChildSet"/> never sees: child classification enumerates directories, so
    /// a file in the root is never classified and would never be asserted. It is Gradle's
    /// <c>gradle.properties</c> in another vendor's folder, and it holds sign-in state.
    /// </summary>
    [Fact]
    public async Task TheNvidiaAccountsFileIsAssertedToSurviveEvenThoughItIsNeverClassified()
    {
        CreateCache(Path.Combine("NVIDIA", "DXCache"));

        var accounts = Path.Combine(_environment.LocalAppData, "NVIDIA", "accounts");
        File.WriteAllText(accounts, "{\"token\":\"<REDACTED>\"}");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(accounts, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(accounts), "NVIDIA sign-in state was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A name recognised under one vendor must not be recognised under another. The roots are
    /// separate declarations precisely so that adding a child to one cannot widen the others.
    /// </summary>
    [Fact]
    public async Task AChildRecognisedForOneVendorIsNotRecognisedForAnother()
    {
        // NVIDIA declares GLCache; AMD, on the evidence available, declares only DxCache.
        var amdGlCache = CreateCache(Path.Combine("AMD", "GLCache"));
        CreateCache(Path.Combine("AMD", "DxCache"));

        var amd = GpuShaderCacheProvider.Roots.Single(r => r.DirectoryName == "AMD");
        Assert.Equal(SafetyTier.DoNotTouch, amd.Children.Classify("GLCache").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(amdGlCache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheRootsStanding()
    {
        var dxCache = CreateCache(Path.Combine("NVIDIA", "DXCache"));
        var unrecognised = CreateCache(Path.Combine("NVIDIA", "Telemetry"));
        var direct3D = CreateCache("D3DSCache");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(dxCache));
        Assert.False(Directory.Exists(direct3D));

        Assert.True(Directory.Exists(Path.Combine(_environment.LocalAppData, "NVIDIA")));
        Assert.True(Directory.Exists(unrecognised));
        Assert.True(Directory.Exists(_environment.LocalAppData));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheNvidiaRootVanished()
    {
        CreateCache(Path.Combine("NVIDIA", "DXCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        var root = Path.Combine(_environment.LocalAppData, "NVIDIA");
        Directory.Delete(root, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.3: a shader cache is a flat store of thousands of blobs, but the profile it sits in can
    /// already be deep. Measurement and deletion must both survive a target whose contents run past
    /// MAX_PATH.
    ///
    /// A smoke test, and knowingly so. <c>docs/todo/after-the-scanner.md</c> establishes that an
    /// outcome-based long-path test discriminates on no machine at all: .NET prepends <c>\\?\</c>
    /// itself to any path of 260 characters or more before calling Win32, so this passes with
    /// <see cref="LongPath.Extended"/> deleted outright. What actually proves Core applies the
    /// prefix is <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c>,
    /// which asserts on the form of every path crossing <see cref="IFileSystem"/>. This one still
    /// earns its place as a crash guard over a deep tree.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var cache = Path.Combine(_environment.LocalAppData, "NVIDIA", "DXCache");

        var deep = cache;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        var blob = Path.Combine(deep, "pipeline.bin");
        Assert.True(blob.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(blob), new byte[4096]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "A shader cache past MAX_PATH was measured as empty.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(blob), "A blob past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(cache));
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
