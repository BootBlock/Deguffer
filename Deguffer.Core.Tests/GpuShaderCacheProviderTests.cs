using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shader-cache provider spans five locations across two of the profile's application-data
/// tiers, so §5.2 has to hold five times over rather than once. These are mostly negative tests:
/// each <c>NVIDIA</c> root holds sign-in state beside its cache, and <c>%LOCALAPPDATA%</c> is the
/// parent of the one target that is a whole directory.
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

    /// <summary>
    /// The same, in the profile's LocalLow tier. NVIDIA keeps a second cache there.
    /// </summary>
    private string CreateLocalLowCache(string relative, int bytes = 4096)
    {
        var directory = Path.Combine(_environment.LocalLowAppData!, relative);
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
        var localLow = CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"), 4096);
        var amd = CreateCache(Path.Combine("AMD", "DxCache"));
        var intel = CreateCache(Path.Combine("Intel", "ShaderCache"));
        var direct3D = CreateCache("D3DSCache");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        string[] expected = [amd, direct3D, intel, dxCache, glCache, localLow];

        // Both sides sorted, because the two NVIDIA roots sit in sibling directories whose names
        // share a prefix and the order that falls out of that is not worth encoding here.
        Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
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
        CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));
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
    /// A junctioned vendor root is enumerated straight through if nobody checks, and every survivor
    /// named for that root resolves through the link and passes. The root is reached by name, so it
    /// needs the same check the Direct3D cache gets.
    /// </summary>
    [Fact]
    public async Task AJunctionedVendorRootIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "DXCache"));
        File.WriteAllBytes(Path.Combine(outside, "DXCache", "pipeline.bin"), new byte[4096]);

        var link = Path.Combine(_environment.LocalAppData, "NVIDIA");
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("NVIDIA", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        // Nothing targeted and something declined, so the row must not read "Already clear".
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "DXCache", "pipeline.bin")),
            "planning looked through a junctioned vendor root and deleted the far side");
    }

    /// <summary>
    /// A junctioned cache is a child the user can see, so a plan that neither offers it nor mentions
    /// it disagrees with the folder. Silently dropping it also made the empty-plan message lie:
    /// presence resolves through a link, so the provider would report no cache while one existed.
    /// </summary>
    [Fact]
    public async Task AJunctionedVendorChildIsNamedRatherThanDroppedSilently()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "pipeline.bin"), new byte[4096]);

        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "NVIDIA"));
        var link = Path.Combine(_environment.LocalAppData, "NVIDIA", "DXCache");
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("DXCache", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "pipeline.bin")),
            "a junctioned cache was deleted through");
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

        var nvidia = GpuShaderCacheProvider.Roots
            .Single(r => r.DirectoryName == "NVIDIA" && r.Area == ProfileArea.LocalAppData);
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

    /// <summary>
    /// Presence is decided by probing a vendor cache <em>by full name</em>, and a full path still
    /// resolves through a directory the account may not list — listing and traversing are separate
    /// rights. So the vendor root can hold <c>DXCache</c>, answer the probe with it, refuse the
    /// listing that would classify it, and leave the plan saying no driver has written a shader
    /// cache for this user.
    /// </summary>
    [Fact]
    public async Task AVendorRootThatWillNotBeListedIsSaidSoRatherThanReportedAsNoCacheAtAll()
    {
        var cache = CreateCache(Path.Combine("NVIDIA", "DXCache"));
        var vendorRoot = Path.Combine(_environment.LocalAppData, "NVIDIA");

        using var denied = new DeniedDirectory(vendorRoot);

        var provider = CreateProvider();

        // The premise: the by-name probe still reaches the cache through the refused vendor root.
        Assert.True(await provider.IsPresentAsync());
        Assert.True(LongPath.DirectoryExists(cache));

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(vendorRoot));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("No graphics driver has written a shader cache"));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// NVIDIA writes a second <c>DXCache</c> under LocalLow. It was measured at much the same size
    /// as the one under <c>%LOCALAPPDATA%</c>, and neither is a link to the other, so a plan that
    /// reaches only the first leaves about half the reclaim on the disk.
    /// </summary>
    [Fact]
    public async Task PlansBothNvidiaCachesWhenTheDriverHasWrittenOneInEachTier()
    {
        var local = CreateCache(Path.Combine("NVIDIA", "DXCache"), 8192);
        var localLow = CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"), 8192);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(local, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(localLow, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(local));
        Assert.False(Directory.Exists(localLow), "the LocalLow shader cache was planned and then left behind.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Explore states a root's reason on its own when it refuses one, with no path beside it, so
    /// two roots whose reasons read identically leave the user unable to tell which folder was
    /// refused. NVIDIA has a root in each of two tiers, which makes that live rather than
    /// theoretical.
    /// </summary>
    [Fact]
    public void EachRootIsRefusedWithAReasonThatNamesWhichFolderItIs()
    {
        var reasons = CreateProvider().ToolRoots.Select(root => root.Reason).ToList();

        Assert.Equal(reasons.Count, reasons.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Presence is answered from the declared paths, so a machine carrying only the LocalLow cache
    /// must still report one rather than nothing at all.
    /// </summary>
    [Fact]
    public async Task ACacheUnderLocalLowAloneIsStillPresence()
    {
        CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// §5.2's unknown case for the LocalLow root, which is a separate declaration from the root of
    /// the same name under <c>%LOCALAPPDATA%</c> and gets no coverage from it.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedLocalLowNvidiaChildIsTier4AndSurvives()
    {
        CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));
        var sibling = CreateLocalLowCache(Path.Combine("NVIDIA", "Telemetry"));

        var root = GpuShaderCacheProvider.Roots
            .Single(r => r.DirectoryName == "NVIDIA" && r.Area == ProfileArea.LocalLowAppData);
        Assert.Equal(SafetyTier.DoNotTouch, root.Children.Classify("Telemetry").Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // Qualified by tier, because an unqualified note would name a folder the user has two of.
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("NVIDIA (LocalLow)\\Telemetry", StringComparison.Ordinal));

        // Not merely absent from the plan — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), "Telemetry was removed alongside the LocalLow cache.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>accounts</c> sits beside the LocalLow cache exactly as it does beside the other one, and
    /// it is a file, so nothing classifies it and only naming it makes §5.6 assert it.
    /// </summary>
    [Fact]
    public async Task TheLocalLowNvidiaAccountsFileIsAssertedToSurviveAsWell()
    {
        CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));

        var accounts = Path.Combine(_environment.LocalLowAppData!, "NVIDIA", "accounts");
        File.WriteAllText(accounts, "{\"token\":\"<REDACTED>\"}");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(accounts, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(accounts), "NVIDIA sign-in state under LocalLow was removed with the cache.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The LocalLow row is deliberately thin: only <c>DXCache</c> was measured there. Declaring
    /// <c>GLCache</c> under <c>%LOCALAPPDATA%</c> must not widen the other root, for the reason
    /// AMD's row does not inherit NVIDIA's names.
    /// </summary>
    [Fact]
    public async Task NvidiaGlCacheIsNotRecognisedUnderLocalLow()
    {
        var glCache = CreateLocalLowCache(Path.Combine("NVIDIA", "GLCache"));
        CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));

        var root = GpuShaderCacheProvider.Roots
            .Single(r => r.DirectoryName == "NVIDIA" && r.Area == ProfileArea.LocalLowAppData);
        Assert.Equal(SafetyTier.DoNotTouch, root.Children.Classify("GLCache").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(glCache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// LocalLow has no <see cref="System.Environment.SpecialFolder"/>, so it comes from a call that
    /// can fail. §5.2 forbids assembling the path by hand when it does: a cache that really is
    /// there goes untouched rather than being reached through a guess at where the tier ought to be.
    /// </summary>
    [Fact]
    public async Task NothingUnderLocalLowIsPlannedWhenWindowsWillNotSayWhereItIs()
    {
        var cache = CreateLocalLowCache(Path.Combine("NVIDIA", "DXCache"));
        _environment.WithNoLocalLow();

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(
            provider.RootPaths,
            path => path.Contains("LocalLow", StringComparison.OrdinalIgnoreCase));

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(cache), "a cache was reached through a guess at where LocalLow is.");
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
