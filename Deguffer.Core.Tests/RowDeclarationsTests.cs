using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Several rows walk one folder, each with its own table: a VS Code user-data folder holds
/// <c>Local State</c>, so both Chromium rows walk it beside the two VS Code rows. Each row spares
/// what its own table does not offer, and what one row spares another may remove. These prove that
/// such a child is left to the row that offers it, that nothing else is, and that §5.6 still asserts
/// everything no row offers.
/// </summary>
public sealed class RowDeclarationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RowDeclarationsTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A folder both kinds of row recognise: Chromium's <c>Local State</c>, and the editor's own
    /// global storage database.
    /// </summary>
    private string CreateEditor()
    {
        var path = Path.Combine(_environment.RoamingAppData, "Code");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        CreateFile(Path.Combine(path, "User", "globalStorage", "state.vscdb"));
        return path;
    }

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[4096]);
        return path;
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    /// <summary>The four rows that walk an editor's folder, as the default planner builds them.</summary>
    private CleanupPlanner EditorRows(RowDeclarations? declarations)
    {
        var discovery = new ChromiumUserDataDiscovery(_environment);
        var runner = new FakeProcessRunner();
        var inspector = FakeProcessInspector.NothingRunning;

        return new CleanupPlanner(
            [
                new ChromiumCacheProvider(
                    _environment, runner, inspector, liveTrees: FakeLiveTreeInspector.NothingLive,
                    discovery: discovery, declarations: declarations),
                new ChromiumServiceWorkerStorageProvider(
                    _environment, runner, inspector, liveTrees: FakeLiveTreeInspector.NothingLive,
                    discovery: discovery, declarations: declarations),
                new VsCodeCacheProvider(_environment, runner, inspector, declarations: declarations),
                new VsCodeLogProvider(_environment, runner, inspector, declarations: declarations),
            ],
            declarations);
    }

    private static async Task<(IReadOnlyList<Finding> Findings, IReadOnlyList<CleanupResult> Results)> CleanEverything(
        CleanupPlanner planner)
    {
        var findings = await planner.PlanAllAsync();

        return (findings, await planner.ExecuteAsync(
            findings,
            [.. findings.Select(f => new Confirmation(f.Provider.Id, f.Provider.Name))]));
    }

    /// <summary>
    /// The reported defect. Each row asserted that the others' caches survived, so the row that
    /// verified after another had removed them reported a §5.6 failure for a run that removed only
    /// what it offered. Each must pass, and the negative must still hold: the editor's state, the
    /// key Chromium decrypts with, and a directory no row recognises are all still standing.
    /// </summary>
    [Fact]
    public async Task EveryRowOverAnEditorsFolderPassesWhenAllAreCleanedTogether()
    {
        var editor = CreateEditor();
        var codeCache = CreateDirectory(Path.Combine(editor, "Code Cache"));
        var cachedData = CreateDirectory(Path.Combine(editor, "CachedData"));
        var logs = CreateDirectory(Path.Combine(editor, "logs"));
        var storage = CreateDirectory(Path.Combine(editor, "Service Worker", "CacheStorage"));
        var backups = CreateDirectory(Path.Combine(editor, "Backups"));

        var (findings, results) = await CleanEverything(EditorRows(new RowDeclarations()));

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Verification!.Passed, $"{r.ProviderId}: {r.Verification.Summary}"));

        Assert.False(Directory.Exists(codeCache));
        Assert.False(Directory.Exists(cachedData));
        Assert.False(Directory.Exists(logs));
        Assert.False(Directory.Exists(storage));

        Assert.True(Directory.Exists(backups), "a directory no row recognises was removed.");
        Assert.True(File.Exists(Path.Combine(editor, "Local State")));
        Assert.True(File.Exists(Path.Combine(editor, "User", "globalStorage", "state.vscdb")));

        // Every row still asserts what no row offers, and asserted it while it was there.
        Assert.All(findings, f => Assert.Contains(f.Plan!.ProtectedPaths, p =>
            p.Path.Equals(backups, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present));
    }

    /// <summary>
    /// A row admitted to no planner excuses nothing, so the false failure is what it reports. This
    /// is the case above with the declarations taken away, and it is what proves that the
    /// declarations, not something else in the run, are what made it pass.
    /// </summary>
    [Fact]
    public async Task RowsThatShareNoDeclarationsStillAssertEachOthersRemovals()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "Code Cache"));
        CreateDirectory(Path.Combine(editor, "CachedData"));

        var (_, results) = await CleanEverything(EditorRows(declarations: null));

        Assert.Contains(results, r => r.Verification is { Passed: false });
    }

    /// <summary>
    /// A row's plan does not assert a child another row offers, and does not count it as left
    /// alone. The note would otherwise tell the user that something was kept which a second row in
    /// the same run removes.
    /// </summary>
    [Fact]
    public async Task AChildAnotherRowOffersIsNeitherAssertedNorCountedAsLeftAlone()
    {
        var editor = CreateEditor();
        var codeCache = CreateDirectory(Path.Combine(editor, "Code Cache"));
        CreateDirectory(Path.Combine(editor, "CachedData"));

        var declarations = new RowDeclarations();
        var vsCode = new VsCodeCacheProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, declarations: declarations);
        var chromium = new ChromiumCacheProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            liveTrees: FakeLiveTreeInspector.NothingLive, declarations: declarations);

        declarations.Admit([vsCode, chromium]);

        var plan = await vsCode.PlanAsync();

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(codeCache, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(editor, "User"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("In 'Code', 1 other item is left alone", StringComparison.Ordinal));
    }

    /// <summary>
    /// The question is about one folder. A directory carrying an offered name somewhere a row does
    /// not declare is nobody's, and a name no table offers is nobody's either.
    /// </summary>
    [Fact]
    public void OnlyANameARowOffersInTheFolderItDeclaresIsOffered()
    {
        var editor = CreateEditor();
        var elsewhere = Path.Combine(_environment.RoamingAppData, "Elsewhere");
        Directory.CreateDirectory(elsewhere);

        var declarations = new RowDeclarations();
        declarations.Admit([new VsCodeCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning)]);

        Assert.True(declarations.OfferedByARow(Path.Combine(editor, "CachedData")));
        Assert.True(declarations.OfferedByARow(Path.Combine(editor, "cacheddata")));
        Assert.False(declarations.OfferedByARow(Path.Combine(editor, "User")));
        Assert.False(declarations.OfferedByARow(Path.Combine(editor, "Code Cache")));
        Assert.False(declarations.OfferedByARow(Path.Combine(elsewhere, "CachedData")));
    }

    /// <summary>
    /// The folder is compared in the form <see cref="LongPath.Configured"/> gives it, so a spelling
    /// with the device prefix or a trailing separator names the same folder (§6.3).
    /// </summary>
    [Fact]
    public void TheFolderIsComparedInItsResolvedForm()
    {
        var editor = CreateEditor();
        var declarations = new RowDeclarations();
        declarations.Admit([new VsCodeCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning)]);

        Assert.True(declarations.OfferedByARow(@"\\?\" + Path.Combine(editor, "CachedData")));
        Assert.True(declarations.OfferedByARow(Path.Combine(editor, ".", "CachedData")));
    }

    /// <summary>
    /// Until a planner admits rows the set is empty, and an empty set excuses nothing: the failure
    /// it can cause is a false alarm, never a survivor left unasserted.
    /// </summary>
    [Fact]
    public void DeclarationsNoPlannerAdmittedOfferNothing()
    {
        var editor = CreateEditor();

        Assert.False(new RowDeclarations().OfferedByARow(Path.Combine(editor, "CachedData")));
    }

    /// <summary>
    /// What a row declares through <see cref="ICleanupProvider.DiscoverToolRootsAsync"/> only adds a
    /// refusal, and a sparing root there recognises nearly everything. Read as an offer, it would
    /// drop survivors nobody removes.
    /// </summary>
    [Fact]
    public void ADiscoveredRootIsNeverReadAsAnOffer()
    {
        var folder = Path.Combine(_environment.LocalAppData, "Tool");
        var declarations = new RowDeclarations();
        declarations.Admit([new DeclaringRow([], discovered: [ToolRoot.Sparing(folder, "Configuration.", ["config"])])]);

        Assert.False(declarations.OfferedByARow(Path.Combine(folder, "anything")));
    }

    /// <summary>
    /// A row whose declaration asked what other rows offer would recurse until the stack ran out,
    /// which ends the process with no account of why. It is refused with one instead.
    /// </summary>
    [Fact]
    public void ARowWhoseDeclarationAsksWhatOthersOfferIsRefused()
    {
        var folder = Path.Combine(_environment.LocalAppData, "Tool");
        var declarations = new RowDeclarations();
        declarations.Admit([new DeclaringRow(() =>
        {
            declarations.OfferedByARow(Path.Combine(folder, "child"));
            return [];
        })]);

        Assert.Throws<InvalidOperationException>(() => declarations.OfferedByARow(Path.Combine(folder, "child")));
    }

    /// <summary>
    /// The first question in a pass reads every row's declaration from inside the plan of whichever
    /// row asked, so a cancelled pass stops there rather than reading the rest.
    /// </summary>
    [Fact]
    public void ACancelledPassStopsReadingTheRows()
    {
        var read = 0;
        var declarations = new RowDeclarations();
        declarations.Admit([new DeclaringRow(() => { read++; return []; })]);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => declarations.OfferedByARow(Path.Combine(_environment.LocalAppData, "Tool", "cache"), cancelled.Token));
        Assert.Equal(0, read);
    }

    /// <summary>
    /// The rows rebuild their declarations for each pass, so an answer kept from the last pass would
    /// miss a folder that appeared since.
    /// </summary>
    [Fact]
    public void AnInvalidatedSetReadsTheRowsAgain()
    {
        var folder = Path.Combine(_environment.LocalAppData, "Tool");
        IReadOnlyList<ToolRoot> roots = [];
        var declarations = new RowDeclarations();
        declarations.Admit([new DeclaringRow(() => roots)]);

        Assert.False(declarations.OfferedByARow(Path.Combine(folder, "cache")));

        roots = [ToolRoot.Folders(folder, "A tool's own folder.", name => name == "cache")];
        declarations.Invalidate();

        Assert.True(declarations.OfferedByARow(Path.Combine(folder, "cache")));
    }

    /// <summary>A row that does nothing but declare.</summary>
    private sealed class DeclaringRow(Func<IReadOnlyList<ToolRoot>> roots, IReadOnlyList<ToolRoot>? discovered = null)
        : ICleanupProvider
    {
        public DeclaringRow(IReadOnlyList<ToolRoot> roots, IReadOnlyList<ToolRoot>? discovered = null)
            : this(() => roots, discovered)
        {
        }

        public string Id => "declaring";

        public string Name => "Declaring";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public StepGrain Grain => StepGrain.Parts;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderDescription Description { get; } = new()
        {
            Application = "A stub, standing in for a real toolchain.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => roots();

        public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolRoot>>(discovered ?? []);

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public void InvalidateCaches()
        {
        }

        public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a declaration.");

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a declaration.");

        public Task<VerificationResult> VerifyAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a declaration.");
    }
}
