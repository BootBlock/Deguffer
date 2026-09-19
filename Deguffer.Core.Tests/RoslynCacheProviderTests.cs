using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using static Deguffer.Core.Tests.Fakes.RoslynCacheFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// Roslyn's solution indexes are where a name proves nothing and the tree beneath it is fixed, so what is
/// recognised is the whole tree: each test of the unrecognised case changes one entry in an otherwise
/// perfect set.
///
/// <para>No test here reaches past <c>MAX_PATH</c>. The recognition's only outputs are whether it matched
/// and a date, so a deep tree could not show which form of the path was used, and the removal is
/// <see cref="DirectoryRemover"/>'s, whose tests assert that form.</para>
/// </summary>
public sealed class RoslynCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RoslynCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Cache => CacheIn(_environment);

    private string Roslyn => Path.GetDirectoryName(Cache)!;

    private string VisualStudio => Path.GetDirectoryName(Roslyn)!;

    private RoslynCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    [Fact]
    public async Task ReportsNotPresentWhereRoslynHasKeptNoIndexes()
    {
        Directory.CreateDirectory(VisualStudio);
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task OffersEachProgramsSetAsAnItemSayingWhatItHolds()
    {
        var devenv = CreateHost(Cache, Host, Solution, OtherSolution);
        var service = CreateHost(Cache, OtherHost, Solution);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(2, plan.TargetedPaths.Count);
        Assert.Contains(devenv, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(service, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0);

        var step = plan.Steps.OfType<DeleteDirectoryStep>()
            .Single(s => s.Path.Equals(devenv, StringComparison.OrdinalIgnoreCase));

        Assert.Contains(new ItemFacet("Program", "devenv.exe"), step.Facets);
        Assert.Contains(new ItemFacet("Solutions", "2"), step.Facets);
    }

    /// <summary>
    /// §5.2 and §5.6 together. No folder above a set is a target, and each is asserted to survive —
    /// Visual Studio's own folder and the unsaved-document recovery beside the cache included.
    /// </summary>
    [Fact]
    public async Task NeverTargetsAFolderAboveTheSetsAndAssertsEachSurvives()
    {
        CreateHost(Cache, Host, Solution);
        Put(Path.Combine(VisualStudio, "BackupFiles", "Unsaved.cs"), 256);

        var plan = await CreateProvider().PlanAsync();

        foreach (var folder in (string[])[VisualStudio, Path.Combine(VisualStudio, "BackupFiles"), Roslyn, Cache])
        {
            Assert.All(plan.TargetedPaths, path =>
                Assert.False(IsAtOrUnder(folder, path), $"{path} would have taken {folder} with it."));
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }
    }

    /// <summary>
    /// §5.6's negative, end to end: a recognised set goes, and a set that is not quite one, Visual Studio's
    /// own data and whatever sits beside the cache all stay.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesARecognisedSetAndLeavesEverythingElseStanding()
    {
        var recognised = CreateHost(Cache, Host, Solution);
        var unrecognised = CreateHost(Cache, OtherHost, Solution);
        var intruder = Put(Path.Combine(unrecognised, "notes.txt"), 64);
        var backup = Put(Path.Combine(VisualStudio, "BackupFiles", "Unsaved.cs"), 256);
        var settings = Put(Path.Combine(VisualStudio, "17.0_testinstance", "privateregistry.bin"), 256);
        var beside = Put(Path.Combine(Roslyn, "unknown.dat"), 32);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains(OtherHost, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unrecognised, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(recognised));

        Assert.True(File.Exists(intruder));
        Assert.True(File.Exists(Path.Combine(IndexIn(unrecognised, Solution), "storage.ide")));
        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(beside));
        Assert.True(Directory.Exists(Cache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's unrecognised case, one departure at a time. The premise is asserted first, so each case
    /// proves that its one change is what refused the set rather than a fixture that never matched.
    /// </summary>
    [Theory]
    [InlineData("a file beside the solutions")]
    [InlineData("a solution folder Roslyn did not name")]
    [InlineData("a file beside sqlite3")]
    [InlineData("a folder beside v2")]
    [InlineData("a version folder named in another case")]
    [InlineData("the database directly under sqlite3")]
    [InlineData("a database named in another case")]
    [InlineData("a companion named in another case")]
    [InlineData("an unrecognised file beside the database")]
    [InlineData("a folder named like a database companion")]
    [InlineData("companions with no database")]
    [InlineData("no index at all")]
    [InlineData("a link in place of a solution")]
    public void ASetDepartingFromRoslynsLayoutAnywhereIsNotRecognised(string departure)
    {
        var host = CreateHost(Cache, Host, Solution);
        Assert.NotNull(RoslynHostDirectory.Read(host));

        Depart(departure, host);

        Assert.Null(RoslynHostDirectory.Read(host));
    }

    [Theory]
    [InlineData("devenv.exe")]                                                 // no checksum
    [InlineData("devenv.exe-TooShort")]                                        // too short to be one
    [InlineData("devenv.exe-TestHost-ChecksumAAAA==")]                         // a dash inside the checksum
    [InlineData("devenv.exe-TestHostChecksumAAAAA==.old")]                     // something after it
    [InlineData("a program name far too long-TestHostChecksumAAAAA==")]        // more than twenty characters first
    public void ASetWhoseNameRoslynDidNotBuildIsNotRecognised(string name)
    {
        var host = CreateHost(Cache, name, Solution);

        Assert.Null(RoslynHostDirectory.Read(host));
    }

    /// <summary>
    /// §7's age column. SQLite writes the database files in place, which moves none of the folders above
    /// them, so the answer has to reach the newest entry however deep it is — here a write-ahead log in the
    /// second of two solutions, under folders that all read as more than a year old.
    /// </summary>
    [Fact]
    public async Task DatesEachSetByItsNewestEntryHoweverDeepThatEntryIs()
    {
        var host = CreateHost(Cache, Host, Solution, OtherSolution);
        var longAgo = DateTime.UtcNow.AddDays(-400);
        var recently = DateTime.UtcNow.AddDays(-3);

        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, longAgo);
        }

        File.SetLastWriteTimeUtc(Path.Combine(IndexIn(host, OtherSolution), "storage.ide-wal"), recently);

        // Last, because creating the files moved every folder above them.
        foreach (var folder in Directory.EnumerateDirectories(host, "*", SearchOption.AllDirectories).Append(host))
        {
            Directory.SetLastWriteTimeUtc(folder, longAgo);
        }

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteDirectoryStep>());

        Assert.NotNull(step.LastWritten);
        Assert.Equal(recently, step.LastWritten!.Value, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The folders count as well, and in the direction that keeps a set: SQLite deletes its write-ahead log
    /// when it closes a database, which moves the folder's timestamp past every file left in it. Each level
    /// of the set in turn is the only thing written recently.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(Solution)]
    [InlineData(Solution + @"\sqlite3")]
    [InlineData(Solution + @"\sqlite3\v2")]
    public async Task AFolderNewerThanEveryFileDatesTheSet(string relative)
    {
        var host = CreateHost(Cache, Host, Solution);
        var longAgo = DateTime.UtcNow.AddDays(-400);
        var recently = DateTime.UtcNow.AddDays(-2);

        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, longAgo);
        }

        foreach (var folder in Directory.EnumerateDirectories(host, "*", SearchOption.AllDirectories).Append(host))
        {
            Directory.SetLastWriteTimeUtc(folder, longAgo);
        }

        Directory.SetLastWriteTimeUtc(Path.Combine(host, relative), recently);

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteDirectoryStep>());

        Assert.NotNull(step.LastWritten);
        Assert.Equal(recently, step.LastWritten!.Value, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The user's guard on recently changed files reaches this provider's measurement as well as its removal:
    /// the set whose files are recent shows them withheld and keeps them, and a set nothing has written to in
    /// a year goes whole. The guard works file by file, so what this proves is about files, not about a set in
    /// use as a whole.
    /// </summary>
    [Fact]
    public async Task UnderTheGuardOnRecentFilesRecentFilesStayAndAStaleSetGoesWhole()
    {
        var stranded = CreateHost(Cache, OtherHost, Solution);

        foreach (var file in Directory.EnumerateFiles(stranded, "*", SearchOption.AllDirectories))
        {
            TempDirectory.Age(file, TimeSpan.FromDays(400));
        }

        var current = CreateHost(Cache, Host, Solution);
        var database = Path.Combine(IndexIn(current, Solution), "storage.ide");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(30), DateTime.UtcNow));

        Assert.True(plan.Steps.OfType<DeleteDirectoryStep>()
            .Single(s => s.Path.Equals(current, StringComparison.OrdinalIgnoreCase))
            .WithheldRecent);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(stranded));
        Assert.True(File.Exists(database));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6 under per-item selection. The set the user did not choose becomes a protected path, or execution
    /// verifies nothing about the choice they made.
    /// </summary>
    [Fact]
    public async Task NarrowingToOneSetProtectsTheOneLeftBehindAndSparesItOnDisk()
    {
        var chosen = CreateHost(Cache, Host, Solution);
        var deselected = CreateHost(Cache, OtherHost, Solution);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var narrowed = plan.NarrowedTo(plan.Steps.OfType<DeleteDirectoryStep>()
            .Where(s => s.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase))
            .ToList());

        Assert.Equal([chosen], narrowed.TargetedPaths);
        Assert.Contains(narrowed.ProtectedPaths, p => p.Path.Equals(deselected, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(narrowed);

        Assert.False(Directory.Exists(chosen));
        Assert.True(File.Exists(Path.Combine(IndexIn(deselected, Solution), "storage.ide")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The §5.6 check has to be a real one: a declined set removed behind the plan's back fails
    /// verification rather than passing because the cache folder survived.
    /// </summary>
    [Fact]
    public async Task VerificationFailsIfADeclinedSetIsRemovedBehindThePlansBack()
    {
        CreateHost(Cache, Host, Solution);
        var declined = CreateHost(Cache, OtherHost, Solution);
        Put(Path.Combine(declined, "notes.txt"), 64);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(declined, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(declined, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfVisualStudiosFolderVanished()
    {
        CreateHost(Cache, Host, Solution);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(VisualStudio, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(VisualStudio, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A link beside the sets is named, never followed, and asserted to survive, because it sits among the
    /// sets that are removed. The far side is a perfect set, so a provider that looked through would take it.
    /// </summary>
    [Fact]
    public async Task ALinkInsideTheCacheIsNamedAndAssertedToSurvive()
    {
        var outside = CreateHost(Path.Combine(_temp.Path, "elsewhere"), Host, Solution);
        Directory.CreateDirectory(Cache);
        var link = Path.Combine(Cache, OtherHost);
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(OtherHost, StringComparison.Ordinal) && n.Message.Contains("link", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.True(plan.WasNotExamined);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(Path.Combine(IndexIn(outside, Solution), "storage.ide")));
    }

    /// <summary>
    /// Either folder this provider owns may be a junction onto another drive. Nothing is looked at through
    /// it, and the far side is laid out as the folder it stands in for, so a provider that did look would
    /// find a recognised set there.
    /// </summary>
    [Theory]
    [InlineData("Roslyn")]
    [InlineData(@"Roslyn\Cache")]
    public async Task AFolderThisProviderOwnsThatIsALinkIsNotLookedThrough(string level)
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        var farCache = level == "Roslyn" ? Path.Combine(elsewhere, "Cache") : elsewhere;
        var outside = CreateHost(farCache, Host, Solution);

        var link = Path.Combine(VisualStudio, level);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(IndexIn(outside, Solution), "storage.ide")));
    }

    [Fact]
    public async Task WarnsWhenVisualStudioIsHoldingTheDatabasesOpen()
    {
        CreateHost(Cache, Host, Solution);

        var provider = new RoslynCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("devenv"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// A listing right is separate from a traverse right, so a cache folder that will not be listed yields a
    /// plan with no steps — which the shell would call "Already clear" about a folder nobody read.
    /// </summary>
    [Fact]
    public async Task ACacheThatWillNotBeListedIsSaidSoRatherThanLeftLookingAlreadyClear()
    {
        CreateHost(Cache, Host, Solution);

        using var denied = new DeniedDirectory(Cache);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(Cache));
        Assert.Empty(plan.TargetedPaths);
    }

    private void Depart(string departure, string host)
    {
        var solution = Path.Combine(host, Solution);
        var index = IndexIn(host, Solution);

        switch (departure)
        {
            case "a file beside the solutions":
                Put(Path.Combine(host, "readme.txt"), 8);
                break;

            case "a solution folder Roslyn did not name":
                CreateIndex(host, "Backup of Sample");
                break;

            case "a file beside sqlite3":
                Put(Path.Combine(solution, "notes.txt"), 8);
                break;

            case "a folder beside v2":
                Directory.CreateDirectory(Path.Combine(solution, "sqlite3", "v1"));
                break;

            case "a version folder named in another case":
                Directory.Delete(index, recursive: true);
                Put(Path.Combine(solution, "sqlite3", "V2", "storage.ide"), 64);
                break;

            case "the database directly under sqlite3":
                Directory.Delete(index, recursive: true);
                Put(Path.Combine(solution, "sqlite3", "storage.ide"), 64);
                break;

            case "a database named in another case":
                File.Delete(Path.Combine(index, "storage.ide"));
                Put(Path.Combine(index, "Storage.ide"), 64);
                break;

            // Beside a correctly named database, so the check that a database is present passes and only the
            // file-name match can refuse it.
            case "a companion named in another case":
                File.Delete(Path.Combine(index, "db.lock"));
                Put(Path.Combine(index, "DB.lock"), 0);
                break;

            case "an unrecognised file beside the database":
                Put(Path.Combine(index, "storage.ide.bak"), 8);
                break;

            case "a folder named like a database companion":
                File.Delete(Path.Combine(index, "storage.ide-shm"));
                Directory.CreateDirectory(Path.Combine(index, "storage.ide-shm"));
                break;

            case "companions with no database":
                File.Delete(Path.Combine(index, "storage.ide"));
                break;

            case "no index at all":
                Directory.Delete(solution, recursive: true);
                break;

            case "a link in place of a solution":
                var outside = CreateHost(Path.Combine(_temp.Path, "elsewhere"), Host, OtherSolution);
                Directory.CreateSymbolicLink(Path.Combine(host, OtherSolution), Path.Combine(outside, OtherSolution));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(departure), departure, "No such departure.");
        }
    }

    private static string Put(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
