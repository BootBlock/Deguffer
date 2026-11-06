using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The caches inside a Visual Studio solution's <c>.vs</c> folder, and nothing else in it. The layout
/// is the one measured across 42 <c>.vs</c> folders: the caches sit beside Copilot conversations, file
/// snapshots and solution options, at three levels, so every test asserts what stayed as well as what
/// went.
/// </summary>
public sealed class VisualStudioSolutionCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public VisualStudioSolutionCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private string Git => _environment.FindExecutable("git")!;

    [Fact]
    public async Task OffersEveryRecognisedCacheAtTierOneAndNothingElse()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([.. folder.Caches.Order(StringComparer.OrdinalIgnoreCase)], [.. plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase)]);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Contains(plan.Steps.OfType<DeleteStep>(), step => step.What == "Find in Files index for App");
        Assert.Contains(plan.Steps.OfType<DeleteStep>(), step => step.What == "C++ browsing database for App");
    }

    /// <summary>
    /// §5.6's negative, on the case it exists for: every entry beside a cache, at every level, is the
    /// user's or is not established, and an over-broad rule would take it with the cache.
    /// </summary>
    [Fact]
    public async Task RemovesTheCachesAndProvesEverythingElseInTheFolderSurvived()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var kept in folder.Kept)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.SolutionFolder);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.SolutionFile);

        // The folders the caches sit in stay standing, so their survival is asserted too: a rule
        // reaching one level too far takes the container, and nothing kept would be missing.
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Vs);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Solution);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Version);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.All(folder.Caches, cache => Assert.False(LongPath.EntryExists(cache), $"{cache} was not removed"));
        Assert.All(folder.Kept, kept => Assert.True(LongPath.EntryExists(kept), $"{kept} was removed"));
        Assert.True(File.Exists(Path.Combine(folder.Solution, "copilot-chat", "92494b03", "sessions", "1")));
        Assert.True(File.Exists(Path.Combine(folder.Version, "Server", "sqlite3", "storage.ide")));
        Assert.True(File.Exists(folder.SolutionFile));
        Assert.True(Directory.Exists(folder.Vs));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// Visual Studio keeps the extension of a <c>.slnx</c> in the folder's name and drops a
    /// <c>.sln</c>'s. A folder named for a solution that is no longer there, as a renamed or converted
    /// solution leaves, says nothing about what it is now, so it is left alone whole.
    /// </summary>
    [Fact]
    public async Task ReadsASlnxSolutionsFolderByItsFullNameAndLeavesAFolderNoSolutionNamesAlone()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"), "App.slnx");
        var orphan = Path.Combine(folder.Vs, "App");
        VisualStudioFolderFixture.WriteBytes(Path.Combine(orphan, "FileContentIndex", "0f8fad5b-d9cb-469f-a165-70867728950e.vsidx"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(Path.Combine(folder.Vs, "App.slnx"), folder.Solution);
        Assert.Contains(folder.FileContentIndex, plan.TargetedPaths);
        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(orphan, path));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == orphan && p.Reason.Contains("No solution", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2's unrecognised case. A folder called <c>.vs</c> with no solution beside it is somebody's
    /// own, whatever it holds, so nothing in it is examined and the whole of it is asserted to survive.
    /// </summary>
    [Fact]
    public async Task AFolderCalledVsWithNoSolutionBesideItIsLeftAloneWhole()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        File.Delete(folder.SolutionFile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Vs);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be confirmed as a Visual Studio solution's .vs folder", StringComparison.Ordinal));
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(folder.Caches, cache => Assert.True(LongPath.EntryExists(cache)));
    }

    /// <summary>
    /// Each cache's evidence, broken on its own. A name is never the evidence: a folder of the right
    /// name holding anything else is left alone, asserted to survive, and the reason says why.
    /// </summary>
    [Theory]
    [InlineData("evaluation-extra-file")]
    [InlineData("evaluation-subfolder")]
    [InlineData("design-time-other-file")]
    [InlineData("index-other-file")]
    [InlineData("index-other-folder")]
    [InlineData("index-without-an-index")]
    [InlineData("index-merges-other-file")]
    [InlineData("headers-other-file")]
    [InlineData("headers-not-automatic")]
    [InlineData("headers-link")]
    [InlineData("design-time-link")]
    [InlineData("index-link")]
    [InlineData("database-not-sqlite")]
    [InlineData("database-with-log")]
    [InlineData("database-with-journal")]
    [InlineData("database-is-a-folder")]
    public async Task ACacheWhoseContentsDoNotMatchIsLeftAlone(string broken)
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));

        var spoilt = broken switch
        {
            "evaluation-extra-file" => Spoil(folder.ProjectEvaluation, () => VisualStudioFolderFixture.Write(Path.Combine(folder.ProjectEvaluation, "notes.txt"), "mine")),
            "evaluation-subfolder" => Spoil(folder.ProjectEvaluation, () => Directory.CreateDirectory(Path.Combine(folder.ProjectEvaluation, "keep"))),
            "design-time-other-file" => Spoil(folder.DesignTimeBuild, () => VisualStudioFolderFixture.Write(Path.Combine(folder.DesignTimeBuild, "build.log"), "mine")),
            "index-other-file" => Spoil(folder.FileContentIndex, () => VisualStudioFolderFixture.Write(Path.Combine(folder.FileContentIndex, "notes.txt"), "mine")),
            "index-other-folder" => Spoil(folder.FileContentIndex, () => Directory.CreateDirectory(Path.Combine(folder.FileContentIndex, "keep"))),
            "index-without-an-index" => Spoil(folder.FileContentIndex, () => File.Delete(folder.IndexFile)),
            "index-merges-other-file" => Spoil(folder.FileContentIndex, () => VisualStudioFolderFixture.Write(Path.Combine(folder.FileContentIndex, "merges", "notes.txt"), "mine")),
            "headers-other-file" => Spoil(folder.PrecompiledHeaders, () => VisualStudioFolderFixture.Write(Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "5820ba93e90ae8c8", "notes.txt"), "mine")),
            "headers-not-automatic" => Spoil(folder.PrecompiledHeaders, () => Directory.Move(Path.Combine(folder.PrecompiledHeaders, "AutoPCH"), Path.Combine(folder.PrecompiledHeaders, "Mine"))),
            "headers-link" => Spoil(folder.PrecompiledHeaders, () => Junction.ToDirectory(Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "elsewhere"), Elsewhere("MAIN.ipch"))),
            "design-time-link" => Spoil(folder.DesignTimeBuild, () => Junction.ToDirectory(Path.Combine(folder.DesignTimeBuild, "elsewhere"), Elsewhere(".dtbcache.v2"))),
            "index-link" => Spoil(folder.FileContentIndex, () => Junction.ToDirectory(Path.Combine(folder.FileContentIndex, "merges", "elsewhere"), Elsewhere("0f8fad5b-d9cb-469f-a165-70867728950e.vsidx"))),
            "database-not-sqlite" => Spoil(folder.BrowseDatabase, () => File.WriteAllText(folder.BrowseDatabase, "not a database")),
            "database-with-log" => Spoil(folder.BrowseDatabase, () => VisualStudioFolderFixture.WriteBytes(folder.BrowseDatabase + "-wal")),
            "database-with-journal" => Spoil(folder.BrowseDatabase, () => VisualStudioFolderFixture.WriteBytes(folder.BrowseDatabase + "-journal")),
            "database-is-a-folder" => Spoil(folder.BrowseDatabase, () =>
            {
                File.Delete(folder.BrowseDatabase);
                VisualStudioFolderFixture.Write(Path.Combine(folder.BrowseDatabase, "mine.txt"), "mine");
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(broken), broken, null),
        };

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(spoilt, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(folder.Caches.Count - 1, plan.TargetedPaths.Count);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(spoilt, StringComparison.OrdinalIgnoreCase));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(LongPath.EntryExists(spoilt));
        Assert.True((await provider.VerifyAsync(plan)).Passed);

        static string Spoil(string path, Action spoil)
        {
            spoil();
            return path;
        }

        // A folder outside the .vs folder holding only what the cache allows, so the link alone is
        // what declines it.
        string Elsewhere(string file)
        {
            var target = Path.Combine(_temp.Path, "elsewhere");
            VisualStudioFolderFixture.WriteBytes(Path.Combine(target, file));
            return target;
        }
    }

    /// <summary>
    /// A cache is recognised only at the one level it was observed at, and only inside a version folder
    /// a known Visual Studio writes. The same name anywhere else is not the same thing.
    /// </summary>
    [Fact]
    public async Task ACacheNameAtAnotherLevelOrUnderAnUnknownVersionIsLeftAlone()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"), version: "v19");
        var misplaced = Path.Combine(folder.Vs, "DesignTimeBuild");
        VisualStudioFolderFixture.WriteBytes(Path.Combine(misplaced, ".dtbcache.v2"));
        var misplacedDatabase = Path.Combine(folder.Solution, "Browse.VC.db");
        VisualStudioFolderFixture.WriteDatabase(misplacedDatabase);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [folder.DesignTimeBuild, folder.FileContentIndex, folder.ProjectEvaluation],
            [.. plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase)]);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Version && p.Reason.Contains("version of Visual Studio", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == misplaced);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == misplacedDatabase);
    }

    /// <summary>A link named like a cache is declined and never followed, so what it points at survives.</summary>
    [Fact]
    public async Task ALinkNamedLikeACacheIsNotFollowed()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        VisualStudioFolderFixture.WriteBytes(Path.Combine(elsewhere, ".dtbcache.v2"));
        Directory.Delete(folder.DesignTimeBuild, recursive: true);
        Junction.ToDirectory(folder.DesignTimeBuild, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(folder.DesignTimeBuild, plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("it is a link to somewhere else", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(Path.Combine(elsewhere, ".dtbcache.v2")));
    }

    /// <summary>
    /// The two ways an open solution shows: Visual Studio working in the solution's folder, and
    /// <c>DevHub.exe</c> holding the index open from somewhere else. Either leaves that <c>.vs</c> folder
    /// whole, and the idle solution beside it is still cleaned.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOpenSolutionsCachesAreNotOffered(bool byWorkingFolder)
    {
        var root = ApproveRoot();
        var open = VisualStudioFolderFixture.Create(Path.Combine(root, "Open"));
        var idle = VisualStudioFolderFixture.Create(Path.Combine(root, "Idle"));

        var live = byWorkingFolder
            ? FakeLiveTreeInspector.NothingLive.WithProgram("devenv", workingDirectory: open.SolutionFolder)
            : FakeLiveTreeInspector.NothingLive.WithHeldFile(open.IndexFile, "DevHub");

        var provider = CreateProvider(live: live);
        var plan = await provider.PlanAsync();

        Assert.Equal(idle.Caches.Count, plan.TargetedPaths.Count);
        Assert.All(plan.TargetedPaths, path => Assert.True(LongPath.Contains(idle.Vs, path)));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == open.Vs);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.StartsWith("Left .vs in Open", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(open.Caches, cache => Assert.True(LongPath.EntryExists(cache)));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The veto asks Windows about every file the caches were proved by, and about the solution folder.
    /// Every file named was listed, so the query says so, and an SQLite companion is named only where
    /// the listing found one: a name nothing listed would be a guess the inspector has to probe.
    /// </summary>
    [Fact]
    public async Task TheVetoIsAskedAboutTheSolutionFolderAndEveryCacheFile()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        VisualStudioFolderFixture.WriteBytes(folder.BrowseDatabase + "-wal");
        var inspector = FakeLiveTreeInspector.NothingLive;

        await CreateProvider(live: inspector).PlanAsync();

        var asked = Assert.Single(inspector.Asked);
        Assert.Equal(folder.Vs, asked.Directory);
        Assert.Equal(folder.SolutionFolder, asked.Project);
        Assert.True(asked.LockFilesListed);
        Assert.DoesNotContain(folder.BrowseDatabase + "-journal", asked.LockFileNames);
        Assert.DoesNotContain(folder.SolutionDatabase + "-wal", asked.LockFileNames);
        Assert.Contains(folder.IndexFile, asked.LockFileNames);
        Assert.Contains(folder.BrowseDatabase, asked.LockFileNames);
        Assert.Contains(Path.Combine(folder.ProjectEvaluation, "app.projects.v10.bin"), asked.LockFileNames);
        Assert.Contains(Path.Combine(folder.FileContentIndex, "read.lock"), asked.LockFileNames);
        Assert.Contains(Path.Combine(folder.DesignTimeBuild, ".dtbcache.v2"), asked.LockFileNames);
        Assert.Contains(Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "5820ba93e90ae8c8", "MAIN.ipch"), asked.LockFileNames);
        Assert.Contains(folder.SolutionDatabase, asked.LockFileNames);
        Assert.Contains(folder.BrowseDatabase + "-wal", asked.LockFileNames);
        Assert.DoesNotContain(Path.Combine(folder.Version, ".suo"), asked.LockFileNames);
        Assert.DoesNotContain(asked.LockFileNames, file => file.Contains("copilot-chat", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A preview lists each folder in a <c>.vs</c> folder once, before the measurement walks the
    /// targets: the veto's lock files, every cache's evidence and every target's age read the same
    /// listing. A C++ solution's <c>ipch\AutoPCH</c> holds one folder per header, each of which was
    /// listed four times, so it has several here.
    /// </summary>
    [Fact]
    public async Task APreviewListsEachFolderInTheVsFolderOnce()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        var automatic = Path.Combine(folder.PrecompiledHeaders, "AutoPCH");
        string[] headers = [Path.Combine(automatic, "5820ba93e90ae8c8"), Path.Combine(automatic, "a1b2c3d4e5f60718"), Path.Combine(automatic, "0f0e0d0c0b0a0908")];
        VisualStudioFolderFixture.WriteBytes(Path.Combine(headers[1], "PCH.ipch"));
        VisualStudioFolderFixture.WriteBytes(Path.Combine(headers[2], "STDAFX.ipch"));
        VisualStudioFolderFixture.WriteBytes(Path.Combine(headers[2], "MAIN.ipch"));

        var listed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reader = new VisualStudioCacheReader(directory =>
        {
            listed[directory] = listed.GetValueOrDefault(directory) + 1;
            return VisualStudioCacheReader.List(directory);
        });

        var plan = await CreateProvider(reader: reader).PlanAsync();

        Assert.Equal([.. folder.Caches.Order(StringComparer.OrdinalIgnoreCase)], [.. plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase)]);
        Assert.All(listed, entry => Assert.True(entry.Value == 1, $"{entry.Key} was listed {entry.Value} times"));

        string[] read =
        [
            folder.Vs, folder.Solution, folder.Version, folder.ProjectEvaluation, folder.FileContentIndex,
            Path.Combine(folder.FileContentIndex, "merges"), folder.DesignTimeBuild, folder.PrecompiledHeaders,
            automatic, .. headers,
        ];

        Assert.All(read, directory => Assert.True(listed.ContainsKey(directory), $"{directory} was not read through the reading"));
    }

    /// <summary>
    /// Every decision is taken from the one reading rather than from the disk again: the evidence, a
    /// database's companions, the lock files and the age. The reading here differs from the disk, so a
    /// decision that read the disk again would come out the other way.
    /// </summary>
    [Fact]
    public async Task TheCachesAreJudgedAskedAboutAndDatedFromTheOneReading()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        var header = Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "5820ba93e90ae8c8");
        var written = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        // Later than anything on the disk, so the age can only be the reading's.
        var future = new DateTime(2099, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var inspector = FakeLiveTreeInspector.NothingLive;

        var reader = new VisualStudioCacheReader(directory =>
        {
            var entries = VisualStudioCacheReader.List(directory);

            return entries is null
                ? null
                : directory.Equals(header, StringComparison.OrdinalIgnoreCase)
                    ? [.. entries, new VisualStudioEntry("notes.txt", Path.Combine(directory, "notes.txt"), FileAttributes.Normal, written.ToFileTimeUtc())]
                    : directory.Equals(folder.Version, StringComparison.OrdinalIgnoreCase)
                        ? [.. entries, new VisualStudioEntry("Browse.VC.db-wal", folder.BrowseDatabase + "-wal", FileAttributes.Normal, written.ToFileTimeUtc())]
                        : directory.Equals(folder.DesignTimeBuild, StringComparison.OrdinalIgnoreCase)
                            ? [.. entries.Select(entry => entry with { NewestFileTime = future.ToFileTimeUtc() })]
                            : entries;
        });

        var provider = CreateProvider(live: inspector, reader: reader);
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(folder.PrecompiledHeaders, plan.TargetedPaths);
        Assert.DoesNotContain(folder.BrowseDatabase, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.PrecompiledHeaders);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.BrowseDatabase);
        Assert.Contains(folder.BrowseDatabase + "-wal", Assert.Single(inspector.Asked).LockFileNames);
        Assert.Contains(Path.Combine(header, "notes.txt"), Assert.Single(inspector.Asked).LockFileNames);

        var designTime = Assert.Single(plan.Steps.OfType<DeleteStep>(), step => step.Path == folder.DesignTimeBuild);
        Assert.Equal(future, designTime.LastWritten);

        // §5.6 on the same reading: what it declined survives the clean, and the rest goes.
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(header));
        Assert.True(File.Exists(folder.BrowseDatabase));
        Assert.False(Directory.Exists(folder.DesignTimeBuild));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The header files are counted from the listing that proved each header folder holds them alone.
    /// The reading here finds the one header folder empty, so the cache holds no header and proves
    /// nothing, while a count that listed the folder again would find one on the disk.
    /// </summary>
    [Fact]
    public async Task PrecompiledHeadersAreCountedFromTheListingThatProvedThem()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));
        var header = Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "5820ba93e90ae8c8");
        var reader = new VisualStudioCacheReader(directory =>
            directory.Equals(header, StringComparison.OrdinalIgnoreCase) ? [] : VisualStudioCacheReader.List(directory));

        var plan = await CreateProvider(reader: reader).PlanAsync();

        Assert.DoesNotContain(folder.PrecompiledHeaders, plan.TargetedPaths);
        Assert.Equal(folder.Caches.Count - 1, plan.TargetedPaths.Count);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.PrecompiledHeaders);
    }

    /// <summary>
    /// §5.2 in a case-sensitive folder, where <c>FileContentIndex</c> and <c>fileContentIndex</c> are
    /// two folders. Each is judged by its own listing: the index is offered, the folder beside it that
    /// holds a file of the user's is not, and both are asked about. Judged by one listing for both,
    /// one of them would be offered or declined on the other's contents, whichever was listed last.
    /// </summary>
    [Fact]
    public async Task FoldersWhoseNamesDifferOnlyInCaseAreJudgedApart()
    {
        var solutionFolder = Path.Combine(ApproveRoot(), "App");
        CaseSensitiveFolder.Create(Path.Combine(solutionFolder, ".vs", "App"));
        var folder = VisualStudioFolderFixture.Create(solutionFolder);
        var decoy = Path.Combine(folder.Solution, "fileContentIndex");
        VisualStudioFolderFixture.WriteBytes(Path.Combine(decoy, "0f8fad5b-d9cb-469f-a165-70867728950e.vsidx"));
        VisualStudioFolderFixture.Write(Path.Combine(decoy, "notes.txt"), "mine");
        var inspector = FakeLiveTreeInspector.NothingLive;

        var provider = CreateProvider(live: inspector);
        var plan = await provider.PlanAsync();

        Assert.Contains(folder.FileContentIndex, plan.TargetedPaths);
        Assert.DoesNotContain(decoy, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == decoy);
        Assert.Contains(Path.Combine(decoy, "notes.txt"), Assert.Single(inspector.Asked).LockFileNames);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(Path.Combine(decoy, "notes.txt")));
        Assert.False(Directory.Exists(folder.FileContentIndex));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>"Could not tell" is not "nothing is using it", so an answer that is not whole offers nothing.</summary>
    [Fact]
    public async Task NothingIsOfferedWhenWhetherAnythingIsUsingItCannotBeTold()
    {
        var folder = VisualStudioFolderFixture.Create(Path.Combine(ApproveRoot(), "App"));

        var plan = await CreateProvider(live: FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == folder.Vs);
    }

    /// <summary>A solution opened between the preview and the clean keeps its caches, and says why.</summary>
    [Fact]
    public async Task ASolutionOpenedAfterThePreviewKeepsItsCaches()
    {
        var root = ApproveRoot();
        var opened = VisualStudioFolderFixture.Create(Path.Combine(root, "Opened"));
        var idle = VisualStudioFolderFixture.Create(Path.Combine(root, "Idle"));
        var live = FakeLiveTreeInspector.NothingLive;

        var provider = CreateProvider(live: live);
        var plan = await provider.PlanAsync();

        Assert.Equal(opened.Caches.Count + idle.Caches.Count, plan.TargetedPaths.Count);

        live.WithProgram("devenv", workingDirectory: opened.SolutionFolder);

        var result = await provider.ExecuteAsync(plan);

        Assert.All(opened.Caches, cache => Assert.True(LongPath.EntryExists(cache), $"{cache} was removed under an open solution"));
        Assert.All(idle.Caches, cache => Assert.False(LongPath.EntryExists(cache)));
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: devenv is working in Opened.");
    }

    /// <summary>
    /// The check at the clean asks about the cache files by name and level, not by the evidence. An
    /// index <c>DevHub.exe</c> is still writing can hold a file the evidence does not allow, and a
    /// solution can be moved away between the two presses. Either way the file it holds is still asked
    /// about, and that solution's caches stay.
    /// </summary>
    [Theory]
    [InlineData("index-changed")]
    [InlineData("solution-moved")]
    public async Task AnIndexHeldAfterThePreviewIsAskedAboutEvenWhenItNoLongerMatches(string change)
    {
        var root = ApproveRoot();
        var opened = VisualStudioFolderFixture.Create(Path.Combine(root, "Opened"));
        var idle = VisualStudioFolderFixture.Create(Path.Combine(root, "Idle"));
        var live = FakeLiveTreeInspector.NothingLive;

        var provider = CreateProvider(live: live);
        var plan = await provider.PlanAsync();

        Assert.Equal(opened.Caches.Count + idle.Caches.Count, plan.TargetedPaths.Count);

        if (change == "index-changed")
        {
            VisualStudioFolderFixture.Write(Path.Combine(opened.FileContentIndex, "merge.tmp"), "being written");
        }
        else
        {
            File.Move(opened.SolutionFile, Path.Combine(_temp.Path, "Opened.sln"));
        }

        live.WithHeldFile(opened.IndexFile, "DevHub");

        await provider.ExecuteAsync(plan);

        Assert.All(opened.Caches, cache => Assert.True(LongPath.EntryExists(cache), $"{cache} was removed while DevHub held the index"));
        Assert.All(idle.Caches, cache => Assert.False(LongPath.EntryExists(cache)));
    }

    /// <summary>A <c>.vs</c> folder git tracks a file in is somebody's committed fixture, and is left alone whole.</summary>
    [Fact]
    public async Task AVsFolderGitTracksAFileInIsLeftAlone()
    {
        var repository = Path.Combine(ApproveRoot(), "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var committed = VisualStudioFolderFixture.Create(Path.Combine(repository, "fixtures", "committed"));
        var ignored = VisualStudioFolderFixture.Create(Path.Combine(repository, "app"));

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding(Git, "ls-files", "fixtures/committed/.vs/App/v17/.suo\0");

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.All(plan.TargetedPaths, path => Assert.True(LongPath.Contains(ignored.Vs, path)));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(committed.Vs, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("tracked in git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoSourceFolderItOffersNothingAndSaysWhatItNeeds()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True(provider.IsAwaitingSourceFolders);
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>Explore refuses a <c>.vs</c> folder the plan holds back as in use (§7.1).</summary>
    [Fact]
    public async Task ExploreRefusesAVsFolderSomethingIsUsing()
    {
        var root = ApproveRoot();
        var open = VisualStudioFolderFixture.Create(Path.Combine(root, "Open"));
        VisualStudioFolderFixture.Create(Path.Combine(root, "Idle"));

        var provider = CreateProvider(live: FakeLiveTreeInspector.NothingLive.WithProgram("devenv", workingDirectory: open.SolutionFolder));

        var declared = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(open.Vs, declared.Path);
    }

    private string ApproveRoot()
    {
        var root = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(root);
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private VisualStudioSolutionCacheProvider CreateProvider(
        FakeProcessRunner? runner = null,
        FakeLiveTreeInspector? live = null,
        VisualStudioCacheReader? reader = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new VisualStudioSolutionCacheProvider(
            _roots,
            reader ?? VisualStudioCacheReader.Default,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            live ?? FakeLiveTreeInspector.NothingLive,
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);
    }
}
