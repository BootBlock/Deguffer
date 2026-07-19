using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// cpptools is the second path-based provider, and the one where §5.2's "unrecognised is Tier 4"
/// does real work: every workspace database directory is hex-named, so the recognised set cannot
/// be widened by pattern-matching a name.
/// </summary>
public sealed class VsCodeCppToolsCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VsCodeCppToolsCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VsCodeCppToolsCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateCacheRoot()
    {
        var root = Path.Combine(_environment.LocalAppData, "Microsoft", "vscode-cpptools");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheExtensionWasNeverUsed()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansThePrecompiledHeaderCacheWithItsMeasuredSize()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 8192);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(root, "ipch")], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Fact]
    public async Task NeverTargetsTheCppToolsRootDirectory()
    {
        CreateCacheRoot();
        var provider = CreateProvider();
        CreateAt(provider.RootPath, "ipch", 1024);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.RootPath, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path => Assert.NotEqual(
            provider.RootPath.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReclaimsAWorkspaceDatabaseRecognisedByItsContents()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        var workspace = CreateWorkspaceDatabase(root, "0123456789abcdef0123456789abcdef");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(workspace, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Leaving", StringComparison.Ordinal)
            && n.Message.Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sidecars and the numbered variant are part of the signature; a directory holding the
    /// full set is still nothing but browse database.
    /// </summary>
    [Theory]
    [InlineData(".BROWSE.VC.DB")]
    [InlineData(".BROWSE.VC.DB-shm")]
    [InlineData(".BROWSE.VC.DB-wal")]
    [InlineData(".BROWSE.VC.2.DB")]
    [InlineData("my-workspace.BROWSE.VC.DB")]
    [InlineData("MY-WORKSPACE.browse.vc.db")]
    public void RecognisesEveryShapeOfBrowseDatabaseFilename(string fileName)
    {
        var directory = _temp.CreateDirectory("signature", Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(Path.Combine(directory, fileName), new byte[64]);

        Assert.True(VsCodeCppToolsCacheProvider.WorkspaceDatabase.Matches(directory));
    }

    /// <summary>
    /// §5.2's unrecognised case, which is the direction that matters: anything the signature does
    /// not vouch for stays Tier 4 rather than being quietly treated as safe.
    /// </summary>
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("BROWSE.VC.DB.bak")]
    [InlineData(".BROWSE.VC.DB.txt")]
    [InlineData("source.cpp")]
    public void ADirectoryHoldingAnythingUnexpectedFailsTheSignature(string intruder)
    {
        var directory = _temp.CreateDirectory("signature", Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(Path.Combine(directory, ".BROWSE.VC.DB"), new byte[64]);
        File.WriteAllBytes(Path.Combine(directory, intruder), new byte[64]);

        Assert.False(
            VsCodeCppToolsCacheProvider.WorkspaceDatabase.Matches(directory),
            $"'{intruder}' beside the database should have disqualified the directory.");
    }

    /// <summary>
    /// The subdirectory is deliberately named like a database file. A signature that checked only
    /// names would wave it through, so this distinguishes "rejects unexpected names" from
    /// "rejects subdirectories" — the real workspace directories are flat.
    /// </summary>
    [Fact]
    public void ADirectoryHoldingASubdirectoryFailsTheSignatureEvenIfItIsNamedLikeADatabase()
    {
        var directory = _temp.CreateDirectory("signature", "with-subdirectory");
        File.WriteAllBytes(Path.Combine(directory, ".BROWSE.VC.DB"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(directory, "nested.BROWSE.VC.DB"));

        Assert.False(VsCodeCppToolsCacheProvider.WorkspaceDatabase.Matches(directory));
    }

    [Fact]
    public void AnEmptyDirectoryFailsTheSignatureBecauseAbsenceOfEvidenceIsNotEvidence()
    {
        var directory = _temp.CreateDirectory("signature", "empty");

        Assert.False(VsCodeCppToolsCacheProvider.WorkspaceDatabase.Matches(directory));
    }

    /// <summary>
    /// §5.6. Now that recognised workspace databases are deleted, the ones left alone are siblings
    /// of the same shape — so the plan has to carry them as protected paths, or execution verifies
    /// only that the root survived and an over-broad rule that took a Tier 4 sibling would pass.
    /// </summary>
    [Fact]
    public async Task ProtectsEveryChildItDeclinedSoVerificationCanCatchOverReach()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        CreateWorkspaceDatabase(root, "aaaabbbbccccddddeeeeffff00001111");
        var unverifiable = CreateAt(root, "22223333444455556666777788889999", 4096);
        var unknown = CreateAt(root, "TelemetryProfile", 512);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unverifiable, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unknown, StringComparison.OrdinalIgnoreCase));

        // The root stays protected, and nothing the plan targets is also claimed as protected.
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(plan.ProtectedPaths.Select(p => p.Path)
            .Intersect(plan.TargetedPaths, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The §5.6 check has to be a real one: if a protected sibling is removed behind the plan's
    /// back, verification must fail rather than reporting success because the root survived.
    /// </summary>
    [Fact]
    public async Task VerificationFailsIfADeclinedSiblingIsRemovedBehindThePlansBack()
    {
        var root = CreateCacheRoot();
        CreateWorkspaceDatabase(root, "aaaabbbbccccddddeeeeffff00001111");
        var unverifiable = CreateAt(root, "22223333444455556666777788889999", 4096);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(unverifiable, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(unverifiable, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesARecognisedWorkspaceAndLeavesAnUnverifiableOneStanding()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 8192);
        var recognised = CreateWorkspaceDatabase(root, "aaaabbbbccccddddeeeeffff00001111");

        // Same hex shape, but it holds something the signature cannot vouch for.
        var unverifiable = CreateWorkspaceDatabase(root, "22223333444455556666777788889999");
        File.WriteAllBytes(Path.Combine(unverifiable, "unexpected.log"), new byte[512]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(recognised));

        // §5.6: the negative half. The root, ipch's parent, and the directory whose contents could
        // not be verified all survive — an over-broad rule would have taken the last of these.
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(unverifiable));
        Assert.True(File.Exists(Path.Combine(unverifiable, "unexpected.log")));
        Assert.True(File.Exists(Path.Combine(unverifiable, ".BROWSE.VC.DB")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task MeasuresAndRemovesAWorkspaceDatabaseNestedPastMaxPath()
    {
        var root = CreateCacheRoot();

        // §6.3. Caveat as elsewhere in this file: on a machine with LongPathsEnabled set this
        // passes without proving the \\?\ prefix is what handled it. It discriminates only where
        // the registry key is absent.
        var workspace = Path.Combine(root, "ffffeeeeddddccccbbbbaaaa99998888");
        var name = new string('w', 200) + ".BROWSE.VC.DB";
        var target = Path.Combine(workspace, name);

        Directory.CreateDirectory(LongPath.Extended(workspace));
        File.WriteAllBytes(LongPath.Extended(target), new byte[4096]);
        Assert.True(target.Length > 260, $"Path was only {target.Length} characters.");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(workspace, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes >= 4096, $"Deep content was not measured: {plan.EstimatedBytes} bytes.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(workspace));
        Assert.True(Directory.Exists(root));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A hex-named child whose contents the signature cannot vouch for. The name alone never
    /// promotes it out of Tier 4 — that is the whole premise of classifying by content instead.
    /// </summary>
    [Fact]
    public async Task LeavesAHexNamedChildAloneWhenItsContentsAreNotRecognised()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        var workspace = CreateAt(root, "0123456789abcdef0123456789abcdef", 65536);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            VsCodeCppToolsCacheProvider.DisposableChildren
                .Classify("0123456789abcdef0123456789abcdef").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(workspace, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path =>
            Assert.False(IsAtOrUnder(workspace, path), $"{path} would have taken the workspace database with it."));
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrecognisedChildIsClassifiedTier4AndLeftAlone()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        var unknown = CreateAt(root, "TelemetryProfile", 4096);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            VsCodeCppToolsCacheProvider.DisposableChildren.Classify("TelemetryProfile").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("TelemetryProfile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenTheEditorIsHoldingTheDatabasesOpen()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);

        var provider = new VsCodeCppToolsCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("Code"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheRootVanished()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(root, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesTheHeadersAndLeavesAnUnverifiableChildStanding()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 8192);
        var workspace = CreateAt(root, "fedcba9876543210fedcba9876543210", 4096);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "ipch")));

        // §5.6: the negative half. The root and the unrecognised sibling both survive.
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(workspace));
        Assert.True(File.Exists(Path.Combine(workspace, "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task MeasuresPrecompiledHeadersNestedPastMaxPath()
    {
        var root = CreateCacheRoot();
        var ipch = Path.Combine(root, "ipch");

        // §6.3: the extension nests per-workspace header directories, and a MAX_PATH truncation
        // here would be a silent partial deletion.
        //
        // Caveat, established by removing LongPath.Extended and watching this still pass: on a
        // machine with LongPathsEnabled set, .NET accepts these paths without the \\?\ prefix, so
        // this asserts the path is handled but cannot prove the prefix is what handled it. It
        // discriminates only where the registry key is absent — which is the machine that matters.
        var deep = ipch;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "header.ipch")), new byte[4096]);
        Assert.True(deep.Length > 260);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([ipch], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes >= 4096, $"Deep content was not measured: {plan.EstimatedBytes} bytes.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(deep));
        Assert.True(Directory.Exists(root));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A hex-named child holding nothing but a browse database — the shape the content signature
    /// recognises. The hex names throughout this file are invented, not taken from a real machine.
    /// </summary>
    private static string CreateWorkspaceDatabase(string root, string hexName)
    {
        var directory = Path.Combine(root, hexName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, ".BROWSE.VC.DB"), new byte[65536]);
        File.WriteAllBytes(Path.Combine(directory, ".BROWSE.VC.DB-shm"), new byte[32768]);
        return directory;
    }

    private static string CreateAt(string root, string child, int bytes)
    {
        var directory = Path.Combine(root, child);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
