using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Code - OSS editor's user-data folder is the second most dangerous neighbourhood a provider
/// works in, after a Chromium profile — and it is the same folder. The editor's caches sit beside
/// <c>User</c>, which holds every workspace's restored state, every extension's stored data and the
/// local undo history of files that were never committed. So these are mostly negative tests. The
/// positive ones only establish that the declared caches are reached at all.
/// </summary>
public sealed class VsCodeCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VsCodeCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VsCodeCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>
    /// A folder holding both markers: Chromium's <c>Local State</c>, which says it is an Electron
    /// application's user-data folder, and the editor's own global storage database, which says
    /// that application is a Code - OSS editor.
    /// </summary>
    private string CreateEditor(string name = "Code")
    {
        var path = Path.Combine(_environment.RoamingAppData, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        CreateFile(Path.Combine(path, "User", "globalStorage", "state.vscdb"));
        return path;
    }

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    private static string CreateFile(string path, int bytes = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoEditorKeepsACache()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// Identification is two positive tests, and neither of them is a cache name. A folder carrying
    /// only one marker is not an editor's, and <c>CachedData</c> is a name any directory anywhere
    /// may hold.
    /// </summary>
    [Theory]
    [InlineData(true, false)]   // an Electron application that is not an editor
    [InlineData(false, true)]   // an editor-shaped tree that Chromium never wrote
    [InlineData(false, false)]  // a folder that merely has a cache name in it
    public async Task AFolderMissingEitherMarkerIsNeverLookedInside(bool chromium, bool editor)
    {
        var impostor = Path.Combine(_environment.RoamingAppData, "SomeOtherApplication");
        var cache = CreateDirectory(Path.Combine(impostor, "CachedData"));

        if (chromium)
        {
            File.WriteAllText(Path.Combine(impostor, "Local State"), "{}");
        }

        if (editor)
        {
            CreateFile(Path.Combine(impostor, "User", "globalStorage", "state.vscdb"));
        }

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Editors());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(cache));
    }

    /// <summary>
    /// A folder existing is not evidence that a cache inside it does. An editor installed and never
    /// run keeps a user-data folder with nothing disposable in it, and reporting that as a source
    /// would offer a row the plan then has nothing to say about.
    /// </summary>
    [Fact]
    public async Task AUserDataFolderWithNoCacheInItIsNotPresence()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "User", "workspaceStorage"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansEveryDeclaredCacheIncludingTheWebviewOnes()
    {
        var editor = CreateEditor();

        var cachedData = CreateDirectory(Path.Combine(editor, "CachedData"));
        var vsixs = CreateDirectory(Path.Combine(editor, "CachedExtensionVSIXs"));
        var extensions = CreateDirectory(Path.Combine(editor, "CachedExtensions"));
        var profiles = CreateDirectory(Path.Combine(editor, "CachedProfilesData"));
        var first = CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));
        var second = CreateDirectory(Path.Combine(editor, "WebStorage", "7", "CacheStorage"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { cachedData, vsixs, extensions, profiles, first, second }
                .Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
    }

    /// <summary>
    /// A webview cache alone is a source. It is the only declared cache that sits three levels down,
    /// so a presence probe that only looked at the folder's own children would report the editor as
    /// keeping nothing and never plan the 982 MB the measurement found in there.
    /// </summary>
    [Fact]
    public async Task AWebviewCacheOnItsOwnIsEnoughToBePresent()
    {
        var editor = CreateEditor();
        var cache = CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(cache, (await provider.PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §5.2. The user-data folder is a tool root in every sense that matters, and so are the two
    /// levels this provider descends through to reach a webview cache: <c>WebStorage</c> holds one
    /// directory per webview, and each of those holds what that view saved beside what it cached.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheUserDataFolderOrTheDirectoriesItDescendsInto()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));

        var plan = await CreateProvider().PlanAsync();

        var roots = new[]
        {
            editor,
            Path.Combine(editor, "WebStorage"),
            Path.Combine(editor, "WebStorage", "42"),
        };

        foreach (var root in roots)
        {
            Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path =>
                Assert.False(WouldTakeWithIt(path, root), $"{path} would have taken {root} with it."));

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// §5.2's dangerous direction. Every one of these sits in the same folder as the caches, four of
    /// them with a cache-shaped name, and <c>User</c> is the single most valuable directory Deguffer
    /// has ever worked beside.
    /// </summary>
    [Theory]
    [InlineData("User")]
    [InlineData("Backups")]
    [InlineData("CachedConfigurations")]
    [InlineData("Dictionaries")]
    [InlineData("blob_storage")]
    [InlineData("Local Storage")]
    [InlineData("Partitions")]
    [InlineData("CachedSomethingNew")]
    public async Task AnUnrecognisedSiblingIsTier4AndIsAssertedToSurvive(string name)
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        var sibling = CreateDirectory(Path.Combine(editor, name));

        Assert.Equal(SafetyTier.DoNotTouch, VsCodeCacheProvider.FolderChildren.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // Not merely absent from the plan — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"{name} was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same rule inside a webview partition, which is where it matters most: a partition holds
    /// the whole Chromium storage set, so <c>Local Storage</c> and <c>IndexedDB</c> are siblings of
    /// the one directory that goes, in the same folder in the same naming style.
    /// </summary>
    [Theory]
    [InlineData("Local Storage")]
    [InlineData("IndexedDB")]
    [InlineData("Session Storage")]
    public async Task AnUnrecognisedChildOfAWebviewPartitionIsTier4AndSurvives(string name)
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));
        var spared = CreateDirectory(Path.Combine(editor, "WebStorage", "42", name));

        Assert.Equal(SafetyTier.DoNotTouch, VsCodeCacheProvider.PartitionChildren.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(spared), $"{name} was removed with the webview cache.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A partition is digits and nothing else, on the pattern the Chromium profiles use. Anything
    /// else under <c>WebStorage</c> is never looked inside — which matters because a folder somebody
    /// made themselves is exactly where a hand-taken copy of a partition would sit.
    /// </summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("42-old")]
    [InlineData("QuotaManager")]
    public async Task ADirectoryThatOnlyLooksLikeAPartitionIsNeverLookedInside(string name)
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        var inside = CreateDirectory(Path.Combine(editor, "WebStorage", name, "CacheStorage"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(inside, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // The folder is a spared sibling of the partitions that are entered, so §5.6 asserts it
        // rather than merely leaving it out.
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(editor, "WebStorage", name), StringComparison.OrdinalIgnoreCase)
            && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(inside), "a hand-made copy of a webview partition was cleared.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The <c>User</c> tree is never classified, because child classification enumerates one
    /// directory at a time and this provider never enters that one. §4.3 calls it the founding
    /// example of user data wearing a cache costume — 14 GB of it on the measured machine — so the
    /// provider names each part of it and a run produces evidence it is still there.
    /// </summary>
    [Fact]
    public async Task TheUserTreeIsAssertedToSurviveEvenThoughItIsNeverClassified()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));

        var workspaces = CreateDirectory(Path.Combine(editor, "User", "workspaceStorage"));
        var global = CreateDirectory(Path.Combine(editor, "User", "globalStorage"));
        var history = CreateDirectory(Path.Combine(editor, "User", "History"));
        var settings = CreateFile(Path.Combine(editor, "User", "settings.json"));
        var database = Path.Combine(editor, VsCodeUserDataDiscovery.IdentifyingFile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { workspaces, global, history, settings, database })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(workspaces));
        Assert.True(Directory.Exists(global));
        Assert.True(Directory.Exists(history));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(database));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same folder is reached by <see cref="ChromiumCacheProvider"/>, which knows the six engine
    /// cache names, and by this one, which knows the editor's own. Neither may reach the other's
    /// children: a Chromium rule that took <c>CachedData</c> would be acting on a name it has no
    /// knowledge of, and this provider taking <c>Code Cache</c> would be the same mistake mirrored.
    /// </summary>
    [Fact]
    public async Task TheChromiumProviderAndThisOneTargetDisjointChildrenOfTheSameFolder()
    {
        var editor = CreateEditor();

        var editorCache = CreateDirectory(Path.Combine(editor, "CachedData"));
        var engineCache = CreateDirectory(Path.Combine(editor, "Code Cache"));

        var mine = await CreateProvider().PlanAsync();
        var chromium = await new ChromiumCacheProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning).PlanAsync();

        Assert.Contains(editorCache, mine.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(engineCache, mine.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(engineCache, chromium.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(editorCache, chromium.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A junctioned <c>WebStorage</c> is the worst case this provider has. The partitions inside it
    /// are reached by enumerating a directory named from a constant, so without the check the
    /// deletion lands wherever the link points, while every §5.6 survivor named inside the folder
    /// resolves through it and passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedWebStorageIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "42", "CacheStorage"));

        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        Directory.CreateSymbolicLink(Path.Combine(editor, "WebStorage"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(outside, "42", "CacheStorage"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(editor, "WebStorage", "42", "CacheStorage"),
            plan.TargetedPaths,
            StringComparer.OrdinalIgnoreCase);

        // Said once, not once per route. A junctioned WebStorage is met as a link child of the
        // folder and as a directory the partition scan refuses to enter.
        Assert.Single(plan.Notes, n =>
            n.Message.Contains("WebStorage", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a junctioned WebStorage and deleted the far side.");
    }

    /// <summary>
    /// The same rule one level down. A partition is reached by enumerating <c>WebStorage</c>, so the
    /// link filtering there is what keeps a redirected partition out of the plan.
    /// </summary>
    [Fact]
    public async Task AJunctionedPartitionIsNeverEntered()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "CacheStorage"));

        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        Directory.CreateDirectory(Path.Combine(editor, "WebStorage"));
        Directory.CreateSymbolicLink(Path.Combine(editor, "WebStorage", "42"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(editor, "WebStorage", "42", "CacheStorage"),
            plan.TargetedPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("42", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "a junctioned webview partition was entered and the far side deleted.");
    }

    /// <summary>
    /// The discovery walk is the only thing standing between a junctioned user-data folder and a
    /// deletion on the far side of it. A cache reached through such a folder is a real directory, so
    /// every later reparse check answers false and every §5.6 survivor resolves through the link and
    /// passes.
    /// </summary>
    [Fact]
    public async Task AJunctionedUserDataFolderIsNeverIdentified()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "CachedData"));
        File.WriteAllText(Path.Combine(outside, "Local State"), "{}");
        CreateFile(Path.Combine(outside, "User", "globalStorage", "state.vscdb"));

        Directory.CreateSymbolicLink(Path.Combine(_environment.RoamingAppData, "Code"), outside);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Editors());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "discovery looked through a junctioned user-data folder and deleted the far side.");
    }

    /// <summary>
    /// A junctioned cache is a child the user can see, so a plan that neither offers it nor mentions
    /// it disagrees with the folder. Dropping it silently would also make the empty-plan message
    /// lie, since presence resolves through the link.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsNamedRatherThanDroppedSilently()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        var editor = CreateEditor();
        Directory.CreateSymbolicLink(Path.Combine(editor, "CachedData"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("CachedData", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        // Nothing was targeted and the only cache was declined, so the shell must not call the row
        // clear.
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")), "a junctioned cache was deleted through.");
    }

    /// <summary>
    /// <c>WebStorage</c> is reached by name, and a full path resolves through a directory the account
    /// may not list — listing and traversing are separate rights. So it can hold every webview cache
    /// on the machine, answer the presence probe through one of them, refuse the listing that would
    /// find the partitions, and leave the provider announcing that no editor keeps a cache at all.
    /// </summary>
    [Fact]
    public async Task AWebStorageThatWillNotBeListedIsSaidSoRatherThanReportedAsNoCacheAtAll()
    {
        var editor = CreateEditor();
        var webStorage = Path.Combine(editor, "WebStorage");
        CreateDirectory(Path.Combine(webStorage, "42", "CacheStorage"));

        using var denied = new DeniedDirectory(webStorage);

        var provider = CreateProvider();

        // The premise: the by-name probe still reaches the webview cache through the refused
        // directory.
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(webStorage, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n =>
            n.Message.Contains("No editor on this machine keeps a cache", StringComparison.Ordinal));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The refusal one level up, at the application-data root. This provider decides presence by
    /// enumerating, so a refusal there answers "no source at all" — and the planner never plans a
    /// provider that reports itself absent. The row then reads "Not installed", which is a stronger
    /// untruth than the "Already clear" that flag exists to stop.
    /// </summary>
    [Fact]
    public async Task AnApplicationDataRootThatWillNotBeListedIsSaidSoRatherThanReportedAsAbsent()
    {
        using var denied = new DeniedDirectory(_environment.RoamingAppData);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(_environment.RoamingAppData, StringComparison.Ordinal));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// §7 scopes the age column to per-workspace and per-project data. Each of these is one whole
    /// cache, so a timestamp on it would be a number with nothing to mean.
    /// </summary>
    [Fact]
    public async Task NoStepCarriesAnAgeBecauseTheseAreWholeCaches()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
    }

    /// <summary>
    /// The tables are designed to grow, and a child declared above Tier 1 would be planned under
    /// this provider's Tier 1 sentence and pre-selected, because a plan carries the provider's tier
    /// rather than the child's. <see cref="DisposableChildSet"/> offers Tier 2 and Tier 3 names, so
    /// nothing else here would catch it.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var children in new[] { VsCodeCacheProvider.FolderChildren, VsCodeCacheProvider.PartitionChildren })
        {
            foreach (var name in children.DisposableNames)
            {
                Assert.Equal(provider.Tier, children.Classify(name).Tier);
            }
        }
    }

    /// <summary>
    /// The editor's own cache names, and only those. A sixth appearing in the table without the
    /// reasoning that belongs to it is exactly how an allow-list stops being one — and this table
    /// sits over the folder holding <c>workspaceStorage</c>.
    /// </summary>
    [Fact]
    public void TheTableDeclaresTheEditorsOwnCacheNamesAndNoOthers()
    {
        Assert.Equal(
            ["CacheStorage", "CachedData", "CachedExtensionVSIXs", "CachedExtensions", "CachedProfilesData"],
            new[] { VsCodeCacheProvider.FolderChildren, VsCodeCacheProvider.PartitionChildren }
                .SelectMany(c => c.DisposableNames)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheFolderStanding()
    {
        var editor = CreateEditor();
        var cachedData = CreateDirectory(Path.Combine(editor, "CachedData"));
        var webview = CreateDirectory(Path.Combine(editor, "WebStorage", "42", "CacheStorage"));
        var user = CreateDirectory(Path.Combine(editor, "User", "workspaceStorage"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(cachedData));
        Assert.False(Directory.Exists(webview));

        Assert.True(Directory.Exists(editor));
        Assert.True(Directory.Exists(Path.Combine(editor, "WebStorage")));
        Assert.True(Directory.Exists(Path.Combine(editor, "WebStorage", "42")));
        Assert.True(Directory.Exists(user));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheUserFolderVanished()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));
        var user = CreateDirectory(Path.Combine(editor, "User", "workspaceStorage"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        Directory.Delete(user, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(user, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.3. A webview cache sits four segments below a folder that is already deep in the profile,
    /// and holds a wide store of small entry files, so measurement and deletion both have to survive
    /// content past MAX_PATH. A smoke test, and knowingly so: .NET prefixes long paths itself before
    /// calling Win32, so what proves Core applies the prefix is
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c>. This one earns
    /// its place as a crash guard over a deep tree.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var editor = CreateEditor();
        var cache = Path.Combine(editor, "WebStorage", "42", "CacheStorage");

        var deep = cache;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('e', 40));
        }

        var entry = Path.Combine(deep, "entry.bin");
        Assert.True(entry.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(entry), new byte[4096]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "A webview cache past MAX_PATH was measured as empty.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(entry), "An entry past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(cache));

        // §6.3's failure mode is a silent *partial* deletion, which is exactly the case where the
        // §5.6 negative earns its place: a truncated path can take the wrong tree with it.
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Whether deleting <paramref name="target"/> would destroy <paramref name="protectedPath"/> —
    /// which is true when the protected path is the target or sits inside it.
    /// </summary>
    private static bool WouldTakeWithIt(string target, string protectedPath) =>
        protectedPath.Equals(target, StringComparison.OrdinalIgnoreCase) ||
        protectedPath.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
