using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two debugger symbol rows: the stores only Microsoft's public servers fill, and every other store.
///
/// <para>Four things have to hold. Only an entry shaped like a downloaded copy is ever removed, and the
/// store, its markers and everything else in it survive the run, asserted by name and refused in Explore,
/// and a store SymStore built is left alone whole. Each store is on exactly one row, and on the Tier 2 row
/// wherever anything that fills it is not one of Microsoft's public servers, or cannot be read. Nothing in
/// a store is offered while a debugger that writes to it runs. And Visual Studio's store in the temporary
/// folder is offered once, on its own row.</para>
/// </summary>
public sealed class DebuggerSymbolCacheProviderTests : IDisposable
{
    private const string Microsoft = "https://msdl.microsoft.com/download/symbols";
    private const string PrivateServer = "https://symbols.example.com/download/symbols";

    /// <summary>A symbol file's signature and age.</summary>
    private const string PdbIndex = "3844DBB920174967BE7AA4A2C20430FA2";

    /// <summary>An image's time stamp and size, in the lower case WinDbg's store was observed to hold.</summary>
    private const string ImageIndex = "CF11A9DBb7000";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    public DebuggerSymbolCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A folder on another drive, where a symbol path keeps its store.</summary>
    private string Store => Path.Combine(_temp.Path, "drive-d", "Symbols");

    private string WinDbgStore => SymbolStoreLayout.WinDbgStore(_system.ProgramData);

    private string VisualStudioStore => SymbolStoreLayout.VisualStudioDefaultStore(_environment.TempPath);

    private (DebuggerSymbolCacheProvider Microsoft, OtherServerSymbolCacheProvider Other) CreateRows(FakeProcessInspector? inspector = null)
    {
        var discovery = new SymbolStoreDiscovery(_environment, _system, _volumes);
        inspector ??= FakeProcessInspector.NothingRunning;

        return (
            new DebuggerSymbolCacheProvider(_environment, new FakeProcessRunner(), inspector, discovery: discovery, system: _system, volumes: _volumes),
            new OtherServerSymbolCacheProvider(_environment, new FakeProcessRunner(), inspector, discovery: discovery, system: _system, volumes: _volumes));
    }

    private void SymbolPath(string value, string variable = "_NT_SYMBOL_PATH") =>
        _environment.WithEnvironmentVariable(variable, value);

    private static string WriteFile(string path, int bytes = 2048)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>An entry as symsrv makes it: one index holding the file the entry is named for.</summary>
    private static string Entry(string parent, string name, string index = PdbIndex, string? file = null)
    {
        var entry = Path.Combine(parent, name);
        WriteFile(Path.Combine(entry, index, file ?? name));
        return entry;
    }

    private static string Ping(string store) => WriteFile(Path.Combine(store, SymbolStoreLayout.PingFile), 0);

    private ExploreActionPolicy Explore(IReadOnlyList<ToolRoot> roots) => new([], [], _volumes, probedRoots: roots);

    private static void AssertProtected(CleanupPlan plan, params string[] paths)
    {
        foreach (var path in paths)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheMicrosoftRowIsTierOneTheOtherTierTwoAndEachIsOneDecision()
    {
        var (microsoft, other) = CreateRows();

        Assert.Equal(SafetyTier.RegenerableCache, microsoft.Tier);
        Assert.Equal(SafetyTier.RegenerableWithCost, other.Tier);
        Assert.Equal(StepGrain.Parts, microsoft.Grain);
        Assert.Equal(StepGrain.Parts, other.Grain);
        Assert.NotEqual(microsoft.Id, other.Id);
    }

    [Fact]
    public async Task NeitherRowIsPresentWhereNoDebuggerKeepsAStore()
    {
        var (microsoft, other) = CreateRows();

        Assert.False(await microsoft.IsPresentAsync());
        Assert.False(await other.IsPresentAsync());
        Assert.True((await microsoft.PlanAsync()).IsEmpty);
        Assert.True((await other.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §5.2 and §5.6: in a store only Microsoft's server fills, each entry shaped like a downloaded copy
    /// is offered whole, a symbol file, an image, a compressed copy and a pointer alike. The store, its
    /// marker, a folder of any other shape and a file at the root all survive the run, on the disk as in
    /// the report, and Explore refuses each of them.
    /// </summary>
    [Fact]
    public async Task OffersOnlyDownloadedCopiesAndEverythingElseInTheStoreSurvives()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");

        var ping = Ping(Store);
        var pdb = Entry(Store, "ntdll.pdb");
        var image = Entry(Store, "Acx01000.sys", ImageIndex);
        var compressed = Entry(Store, "wow64.dll", ImageIndex, "wow64.dl_");
        var pointer = Entry(Store, "combase.pdb", file: SymbolStoreLayout.PointerFile);
        var noExtension = WriteFile(Path.Combine(Store, "notes", PdbIndex, "notes"));
        var notes = Path.Combine(Store, "notes");
        var notAnIndex = Entry(Store, "mine.pdb", index: "latest");
        var strangerInIndex = Entry(Store, "user32.pdb");
        var stranger = WriteFile(Path.Combine(strangerInIndex, PdbIndex, "readme.txt"));
        var atRoot = WriteFile(Path.Combine(Store, "readme.txt"));

        var (microsoft, other) = CreateRows();

        Assert.True(await microsoft.IsPresentAsync());
        Assert.False(await other.IsPresentAsync());

        var plan = await microsoft.PlanAsync();

        Assert.Equal(
            new[] { pdb, image, compressed, pointer }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        AssertProtected(plan, Store, ping, notes, notAnIndex, strangerInIndex, atRoot);

        var policy = Explore(await microsoft.DiscoverToolRootsAsync());

        foreach (var survivor in new[] { Store, ping, notes, notAnIndex, strangerInIndex, atRoot })
        {
            Assert.False(policy.MayRemove(survivor).IsAllowed, $"Explore would remove '{survivor}'");
        }

        var result = await microsoft.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(new[] { pdb, image, compressed, pointer }, entry => Assert.False(Directory.Exists(entry), entry));
        Assert.All(new[] { ping, noExtension, Path.Combine(notAnIndex, "latest", "mine.pdb"), stranger, atRoot },
            kept => Assert.True(File.Exists(kept), $"'{kept}' did not survive the run"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A store with <c>index2.txt</c> keeps each entry under the first two letters of its name. Only an
    /// entry under its own letters is offered, and the two-letter folders and the marker survive.
    /// </summary>
    [Fact]
    public async Task OffersOnlyEntriesUnderTheirOwnLettersInATwoLevelStore()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");

        var marker = WriteFile(Path.Combine(Store, SymbolStoreLayout.TwoLevelFile), 0);
        var level = Path.Combine(Store, "nt");
        var offered = Entry(level, "ntdll.pdb");
        var misplaced = Entry(level, "kernel32.pdb");
        var topLevel = Entry(Store, "user32.pdb");
        var longName = Entry(Path.Combine(Store, "ntd"), "ntdll.pdb");

        var (microsoft, _) = CreateRows();
        var plan = await microsoft.PlanAsync();

        Assert.Equal([offered], plan.TargetedPaths);
        AssertProtected(plan, Store, marker, level, misplaced, topLevel, Path.GetDirectoryName(longName)!);

        var policy = Explore(await microsoft.DiscoverToolRootsAsync());

        Assert.True(policy.MayRemove(offered).IsAllowed, "Explore refused an entry the plan offers");
        Assert.False(policy.MayRemove(level).IsAllowed);
        Assert.False(policy.MayRemove(misplaced).IsAllowed);

        var result = await microsoft.ExecuteAsync(plan);

        Assert.False(Directory.Exists(offered));
        Assert.True(Directory.Exists(misplaced));
        Assert.True(File.Exists(marker));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// An entry is dated by <see cref="DirectoryAge"/>'s rule, the newest of its own time and its index
    /// folders', from the listings that recognised it, whichever half is the newer. The examination is the
    /// pass's, so the entry or an index folder written after it, within the same pass, is not seen:
    /// nothing lists the entry again to date it.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DatesEachEntryFromTheListingsThatRecognisedIt(bool twoLevel, bool ownTimeNewest)
    {
        SymbolPath($"srv*{Store}*{Microsoft}");

        var parent = Store;
        if (twoLevel)
        {
            WriteFile(Path.Combine(Store, SymbolStoreLayout.TwoLevelFile), 0);
            parent = Path.Combine(Store, "nt");
        }

        var entry = Entry(parent, "ntdll.pdb");
        var older = Path.Combine(entry, PdbIndex);
        var newer = Path.GetDirectoryName(WriteFile(Path.Combine(entry, "4844DBB920174967BE7AA4A2C20430FA2", "ntdll.pdb")))!;
        var now = DateTime.UtcNow;
        var newest = now - TimeSpan.FromDays(10);

        Directory.SetLastWriteTimeUtc(older, now - TimeSpan.FromDays(40));
        Directory.SetLastWriteTimeUtc(newer, ownTimeNewest ? now - TimeSpan.FromDays(30) : newest);
        Directory.SetLastWriteTimeUtc(entry, ownTimeNewest ? newest : now - TimeSpan.FromDays(30));

        var (microsoft, _) = CreateRows();
        await microsoft.DiscoverToolRootsAsync();

        Directory.SetLastWriteTimeUtc(ownTimeNewest ? entry : newer, now);

        var step = Assert.Single((await microsoft.PlanAsync()).Steps);

        Assert.Equal(newest, step.LastWritten);
    }

    /// <summary>
    /// §5.2 and §5.6 across a store of many entries, which are recognised several at a time: every entry
    /// shaped like a downloaded copy is offered, and the store, its markers, its two-letter folders and
    /// every entry of any other shape, an index that is a link included, survive the run and are asserted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffersEveryCopyInAStoreOfManyEntriesAndEverythingElseSurvives(bool twoLevel)
    {
        SymbolPath($"srv*{Store}*{Microsoft}");

        var ping = Ping(Store);
        string[] markers = twoLevel ? [ping, WriteFile(Path.Combine(Store, SymbolStoreLayout.TwoLevelFile), 0)] : [ping];
        var parent = twoLevel ? Path.Combine(Store, "mo") : Store;
        List<string> offered = [];
        List<string> unrecognised = [];

        for (var i = 0; i < 60; i++)
        {
            var name = $"module{i:D2}.pdb";

            switch (i % 6)
            {
                case 1:
                    unrecognised.Add(Entry(parent, name, index: "latest"));
                    break;

                case 3:
                    var stranger = Entry(parent, name);
                    WriteFile(Path.Combine(stranger, PdbIndex, "readme.txt"));
                    unrecognised.Add(stranger);
                    break;

                case 5:
                    var linked = Path.Combine(parent, name);
                    Directory.CreateDirectory(linked);
                    DirectoryLink.Create(DirectoryLinkKind.Junction, Path.Combine(linked, PdbIndex), Entry(Path.Combine(_temp.Path, "elsewhere"), name));
                    unrecognised.Add(linked);
                    break;

                default:
                    offered.Add(Entry(parent, name));
                    break;
            }
        }

        var (microsoft, _) = CreateRows();
        var plan = await microsoft.PlanAsync();

        Assert.Equal(offered.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertProtected(plan, [Store, .. markers, .. unrecognised, .. twoLevel ? [parent] : Array.Empty<string>()]);

        var result = await microsoft.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(offered, entry => Assert.False(Directory.Exists(entry), $"{entry} was offered and is still there"));
        Assert.All(unrecognised, entry => Assert.True(Directory.Exists(entry), $"{entry} was not recognised and was taken"));
        Assert.All(markers, marker => Assert.True(File.Exists(marker)));
        Assert.True(Directory.Exists(parent));
    }

    /// <summary>
    /// §5.2's hard refusal: a store holding <c>000Admin</c> is one SymStore built, so nothing in it is
    /// offered, however much it looks like a cache, and Explore refuses all of it.
    /// </summary>
    [Fact]
    public async Task LeavesAStoreSymStoreBuiltAloneWhole()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");

        var history = WriteFile(Path.Combine(Store, SymbolStoreLayout.AdministrationFolder, "history.txt"));
        var entry = Entry(Store, "ntdll.pdb");

        var (microsoft, _) = CreateRows();
        var plan = await microsoft.PlanAsync();

        Assert.Empty(plan.Steps);
        AssertProtected(plan, Store);
        Assert.Contains(plan.Notes, note => note.Message.Contains("SymStore", StringComparison.Ordinal));

        var policy = Explore(await microsoft.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(entry).IsAllowed);
        Assert.False(policy.MayRemove(Path.GetDirectoryName(history)!).IsAllowed);

        var result = await microsoft.ExecuteAsync(plan);

        Assert.True(Directory.Exists(entry));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A store any other server fills is on the Tier 2 row, and never on the Tier 1 row.</summary>
    [Theory]
    [InlineData(PrivateServer)]
    [InlineData(@"\\builds\symbols")]
    public async Task PutsAStoreAnotherServerFillsOnTheTierTwoRow(string source)
    {
        SymbolPath($"srv*{Store}*{Microsoft};srv*{Store}*{source}");
        var entry = Entry(Store, "ntdll.pdb");

        var (microsoft, other) = CreateRows();

        Assert.False(await microsoft.IsPresentAsync());
        Assert.Empty((await microsoft.PlanAsync()).Steps);
        Assert.Equal([entry], (await other.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A cache element keeps a copy of what every element to its right loads, so one store can be filled
    /// from Microsoft and from another server at once. One server that is not Microsoft's is enough to put
    /// it on the Tier 2 row.
    /// </summary>
    [Fact]
    public async Task PutsAStoreFilledFromMicrosoftAndAnotherServerOnTheTierTwoRow()
    {
        SymbolPath($"cache*{Store};srv*{Microsoft};srv*{PrivateServer}");
        var entry = Entry(Store, "ntdll.pdb");

        var (microsoft, other) = CreateRows();

        Assert.Empty((await microsoft.PlanAsync()).Steps);
        Assert.Equal([entry], (await other.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// A folder the symbol path fills from Microsoft and Visual Studio's settings also name is filled from
    /// servers Deguffer cannot read as well, so it is on the Tier 2 row alone.
    /// </summary>
    [Fact]
    public async Task PutsAFolderTwoSettingsNameOnTheRowTheirSourcesNeed()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");
        VisualStudioSettings(Store);
        var entry = Entry(Store, "ntdll.pdb");

        var (microsoft, other) = CreateRows();

        Assert.Empty((await microsoft.PlanAsync()).Steps);
        Assert.Equal([entry], (await other.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// WinDbg's own store is on the Tier 2 row, because WinDbg keeps its servers where Deguffer cannot
    /// read them. While WinDbg runs nothing in it is offered, and it is protected and refused in Explore;
    /// Visual Studio running does not hold it.
    /// </summary>
    [Fact]
    public async Task HoldsWinDbgsOwnStoreWhileWinDbgRunsAndOnlyThen()
    {
        Ping(WinDbgStore);
        var entry = Entry(WinDbgStore, "CRYPT32.dll", ImageIndex);

        var (_, held) = CreateRows(new FakeProcessInspector("DbgX.Shell"));
        var heldPlan = await held.PlanAsync();

        Assert.Empty(heldPlan.Steps);
        Assert.True(heldPlan.WasNotExamined);
        AssertProtected(heldPlan, WinDbgStore);
        Assert.False(Explore(await held.DiscoverToolRootsAsync()).MayRemove(entry).IsAllowed);

        var (_, free) = CreateRows(new FakeProcessInspector("devenv"));
        var plan = await free.PlanAsync();

        Assert.Equal([entry], plan.TargetedPaths);

        var result = await free.ExecuteAsync(plan);

        Assert.False(Directory.Exists(entry));
        Assert.True(Directory.Exists(WinDbgStore));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The clean asks again before it removes each entry: a debugger started after the preview leaves the
    /// store as it was.
    /// </summary>
    [Fact]
    public async Task LeavesTheStoreWhereADebuggerStartsAfterThePreview()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");
        var entry = Entry(Store, "ntdll.pdb");

        var inspector = new FakeProcessInspector();
        var (microsoft, _) = CreateRows(inspector);
        var plan = await microsoft.PlanAsync();

        Assert.Equal([entry], plan.TargetedPaths);

        inspector.WithRunning("windbg");
        var result = await microsoft.ExecuteAsync(plan);

        Assert.True(Directory.Exists(entry), "an entry was removed while a debugger that writes to its store ran");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>Visual Studio's own setting, read from a settings file that opens with a comment.</summary>
    [Fact]
    public async Task ReadsVisualStudiosMovedCacheFromItsSettings()
    {
        VisualStudioSettings(Store);
        var entry = Entry(Store, "System.Private.CoreLib.pdb");

        var (_, other) = CreateRows();

        Assert.Equal([entry], (await other.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// An older Visual Studio keeps no <c>settings.json</c>, so its setting cannot be read, and the plan
    /// says that only the default folder was looked in rather than staying silent.
    /// </summary>
    [Fact]
    public async Task SaysAnOlderVisualStudiosMovedCacheIsNotLookedFor()
    {
        var instance = Path.Combine(VisualStudioSymbolSettings.InstancesRoot(_environment.LocalAppData), "17.0_5e6f7a8b");
        Directory.CreateDirectory(instance);
        Entry(VisualStudioStore, "ntdll.pdb");

        var (_, other) = CreateRows();
        var plan = await other.PlanAsync();

        Assert.Contains(plan.Notes, note => note.Message.Contains(instance, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A settings file that is not JSON is said out loud rather than read as naming nothing.</summary>
    [Fact]
    public async Task SaysWhereVisualStudiosSettingsCouldNotBeRead()
    {
        var file = WriteFile(Path.Combine(VisualStudioSymbolSettings.InstancesRoot(_environment.LocalAppData), "18.0_1a2b3c4d", VisualStudioSymbolSettings.SettingsFileName));
        File.WriteAllText(file, "{ \"debugging.symbols.location.cachePath\": ");
        Ping(WinDbgStore);

        var (_, other) = CreateRows();
        var plan = await other.PlanAsync();

        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning && note.Message.Contains(file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Visual Studio keeps its store in the temporary folder by default. This row claims the store whole,
    /// so the temporary-files row neither takes nor counts any of it, and the two rows' runs both verify
    /// in either order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TakesVisualStudiosStoreInTheTemporaryFolderOnceInEitherOrder(bool temporaryFirst)
    {
        var entry = Entry(VisualStudioStore, "ntdll.pdb");
        var unrecognised = WriteFile(Path.Combine(VisualStudioStore, "notes", "todo.txt"));
        TempDirectory.Age(Path.Combine(entry, PdbIndex, "ntdll.pdb"), TimeSpan.FromDays(30));
        TempDirectory.Age(unrecognised, TimeSpan.FromDays(30));
        var abandoned = TempDirectory.Age(WriteFile(Path.Combine(_environment.TempPath, "abandoned.tmp"), 1024), TimeSpan.FromDays(30));

        var (microsoft, other) = CreateRows();

        Assert.Equal([VisualStudioStore], await other.ClaimedEntriesAsync([_environment.TempPath]));
        Assert.Empty(await microsoft.ClaimedEntriesAsync([_environment.TempPath]));

        var temporary = new TempDirectoryProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(AppPreferences.Default),
            tenants: [microsoft, other],
            volumes: _volumes);

        var storePlan = await other.PlanAsync();
        var temporaryPlan = await temporary.PlanAsync();

        Assert.Equal([entry], storePlan.TargetedPaths);
        Assert.Equal(1024, temporaryPlan.EstimatedBytes);

        var reach = RunReach.Of([storePlan, temporaryPlan]);
        var residue = new RunResidue();
        var order = temporaryFirst
            ? new (ICleanupProvider Provider, CleanupPlan Plan)[] { (temporary, temporaryPlan), (other, storePlan) }
            : [(other, storePlan), (temporary, temporaryPlan)];

        foreach (var (provider, plan) in order)
        {
            var result = await provider.ExecuteAsync(plan, reach, residue);
            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(Directory.Exists(entry));
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(unrecognised), "an unrecognised folder in the store was taken");
        Assert.True(Directory.Exists(VisualStudioStore));
    }

    /// <summary>
    /// A folder the symbol path reads as a store of its own, at the end of a chain, is somebody's symbols,
    /// so it is never offered as a cache even where another chain copies into it.
    /// </summary>
    [Fact]
    public async Task LeavesAFolderTheSymbolPathAlsoReadsAsItsOwnStore()
    {
        var near = Path.Combine(_temp.Path, "drive-c", "Near");
        SymbolPath($"srv*{Store}*{Microsoft};srv*{near}*{Store}");
        var mine = Entry(Store, "mine.pdb");
        var copy = Entry(near, "mine.pdb");

        var (microsoft, other) = CreateRows();

        Assert.Empty((await microsoft.PlanAsync()).Steps);
        Assert.Contains((await microsoft.PlanAsync()).Notes, note => note.Message.Contains(Store, StringComparison.OrdinalIgnoreCase));

        // The near store is a copy of somebody's own folder, so it is the other row's.
        var plan = await other.PlanAsync();
        Assert.Equal([copy], plan.TargetedPaths);

        var result = await other.ExecuteAsync(plan);

        Assert.True(Directory.Exists(mine), "a store the symbol path reads as its own lost an entry");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A store named by a relative path, or on a network share, is left alone and said so.</summary>
    [Theory]
    [InlineData("Symbols", "not a full path")]
    [InlineData(@"\\builds\cache", "network share")]
    public async Task LeavesAStoreItCannotOwnAloneAndSaysSo(string store, string why)
    {
        SymbolPath($"srv*{store}*{Microsoft}");

        var (microsoft, _) = CreateRows();
        var plan = await microsoft.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note =>
            note.Message.Contains(store, StringComparison.OrdinalIgnoreCase) && note.Message.Contains(why, StringComparison.Ordinal));
    }

    /// <summary>
    /// The default store is in the home folder of whichever debugger reads the path, so it is looked for
    /// only where <c>DBGHELP_HOMEDIR</c> says, and there it is filled by every debugger's own servers too.
    /// </summary>
    [Fact]
    public async Task FindsTheDefaultStoreOnlyWhereTheHomeVariableSaysAndPutsItOnTheTierTwoRow()
    {
        SymbolPath($"srv**{Microsoft}");

        var (unset, _) = CreateRows();
        Assert.Contains((await unset.PlanAsync()).Notes, note => note.Message.Contains("default symbol store", StringComparison.Ordinal));

        var home = Path.Combine(_temp.Path, "drive-d", "Debuggers");
        _environment.WithEnvironmentVariable(SymbolStoreLayout.HomeVariable, home);
        var entry = Entry(Path.Combine(home, SymbolStoreLayout.DefaultStoreName), "ntdll.pdb");

        var (microsoft, other) = CreateRows();

        Assert.Empty((await microsoft.PlanAsync()).Steps);
        Assert.Equal([entry], (await other.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.2: a link where an entry was is declined, not followed, and what it leads to survives.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DeclinesALinkWhereAnEntryWas(DirectoryLinkKind kind)
    {
        SymbolPath($"srv*{Store}*{Microsoft}");
        var elsewhere = Entry(Path.Combine(_temp.Path, "elsewhere"), "ntdll.pdb");
        var link = Path.Combine(Store, "ntdll.pdb");
        Directory.CreateDirectory(Store);
        DirectoryLink.Create(kind, link, elsewhere);

        var (microsoft, _) = CreateRows();
        var plan = await microsoft.PlanAsync();

        Assert.Empty(plan.Steps);
        AssertProtected(plan, link);

        var result = await microsoft.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, PdbIndex, "ntdll.pdb")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A store inside another is its own row's: the outer store neither offers nor asserts it, so both
    /// rows' runs verify.
    /// </summary>
    [Fact]
    public async Task LeavesAStoreInsideAnotherToItsOwnRow()
    {
        var inner = Path.Combine(Store, "private");
        SymbolPath($"srv*{Store}*{Microsoft}");
        SymbolPath($"srv*{inner}*{PrivateServer}", "_NT_ALT_SYMBOL_PATH");
        var outerEntry = Entry(Store, "ntdll.pdb");
        var innerEntry = Entry(inner, "app.pdb");

        var (microsoft, other) = CreateRows();
        var outerPlan = await microsoft.PlanAsync();
        var innerPlan = await other.PlanAsync();

        Assert.Equal([outerEntry], outerPlan.TargetedPaths);
        Assert.DoesNotContain(outerPlan.ProtectedPaths, p => p.Path.Equals(inner, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([innerEntry], innerPlan.TargetedPaths);

        var reach = RunReach.Of([outerPlan, innerPlan]);
        var residue = new RunResidue();

        Assert.True((await microsoft.ExecuteAsync(outerPlan, reach, residue)).Verification!.Passed);
        Assert.True((await other.ExecuteAsync(innerPlan, reach, residue)).Verification!.Passed);
        Assert.True(Directory.Exists(inner));
    }

    /// <summary>§7.4: the places named without planning hold every entry each row's plan removes.</summary>
    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlansClean()
    {
        SymbolPath($"srv*{Store}*{Microsoft}");
        Entry(Store, "ntdll.pdb");
        Entry(WinDbgStore, "CRYPT32.dll", ImageIndex);
        Entry(VisualStudioStore, "System.Private.CoreLib.pdb");

        var (microsoft, other) = CreateRows();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(microsoft));
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(other));
    }

    /// <summary>One Visual Studio 2026 instance's settings, in the shape it writes them, naming <paramref name="folder"/>.</summary>
    private void VisualStudioSettings(string folder)
    {
        var file = WriteFile(Path.Combine(
            VisualStudioSymbolSettings.InstancesRoot(_environment.LocalAppData), "18.0_1a2b3c4d", VisualStudioSymbolSettings.SettingsFileName));

        File.WriteAllText(file, string.Join("\n",
        [
            "/* Visual Studio Settings File */",
            "{",
            "  \"projectsAndSolutions.locations.projectTemplates\": \"C:\\\\Users\\\\testuser\\\\Templates\",",
            $"  \"{VisualStudioSymbolSettings.CachePathKey}\": \"{folder.Replace(@"\", @"\\", StringComparison.Ordinal)}\",",
            "}",
        ]));
    }
}
