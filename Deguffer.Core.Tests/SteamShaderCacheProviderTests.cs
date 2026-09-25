using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Steam's per-game shader caches, which sit inside every game library rather than in one place, and
/// inside the folder that holds the games themselves.
///
/// <para>Three things have to hold. Every library Steam's own list names is reached, and a list
/// that cannot be read is said out loud rather than read as "only one library". Only a child of
/// <c>shadercache</c> named as a Steam application id is ever a target. And the games, the
/// download in progress and Steam's records of them all survive a run, asserted by name.</para>
/// </summary>
public sealed class SteamShaderCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SteamShaderCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string LocalRoot => Path.Combine(_environment.LocalAppData, "Steam");

    private string InstallRoot => Path.Combine(_temp.Path, "games", "Steam");

    /// <summary>A second library, on what would be another drive.</summary>
    private string SecondLibrary => Path.Combine(_temp.Path, "second-drive", "SteamLibrary");

    private SteamShaderCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    /// <summary>The install, recorded as Steam records it, with the client beside it.</summary>
    private string RegisterInstall()
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllBytes(Path.Combine(InstallRoot, "steam.exe"), new byte[64]);

        _environment.WithRegistryValue(
            SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, InstallRoot.Replace('\\', '/'));

        return InstallRoot;
    }

    /// <summary>
    /// Steam's list of libraries in its current layout, with every backslash doubled as Steam's
    /// writer doubles them, so the fixture is the file a real machine holds. Unescaping itself is
    /// proved in <see cref="SteamKeyValuesTests"/>.
    /// </summary>
    private void WriteLibraryList(params string[] libraries)
    {
        var blocks = libraries.Select((library, index) => $$"""
                "{{index}}"
                {
                    "path"		"{{library.Replace(@"\", @"\\")}}"
                    "label"		""
                    "contentid"		"1234567890"
                    "apps"
                    {
                        "440"		"1000"
                    }
                }
            """);

        WriteList($"\"libraryfolders\"\n{{\n{string.Join("\n", blocks)}\n}}\n");
    }

    private void WriteList(string text)
    {
        var steamapps = Path.Combine(InstallRoot, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), text);
    }

    /// <summary>One game's shader cache, laid out as the Steam client lays it out.</summary>
    private static string CacheFor(string library, string appId)
    {
        var cache = Path.Combine(library, "steamapps", "shadercache", appId);
        Populate(Path.Combine(cache, "fozpipelinesv6"));
        return cache;
    }

    private static string Manifest(string library, string appId, string name)
    {
        var path = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            "AppState"
            {
                "appid"		"{{appId}}"
                "name"		"{{name}}"
                "installdir"		"{{name}}"
            }
            """);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSteam()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(provider.ToolRoots);
    }

    /// <summary>
    /// The machine the issue was measured on: pre-caching off, and <c>shadercache</c> there and
    /// empty. That is no row, because a row would report a source with nothing in it.
    /// </summary>
    [Fact]
    public async Task AnEmptyShaderCacheFolderIsNoRow()
    {
        var install = RegisterInstall();
        Directory.CreateDirectory(Path.Combine(install, "steamapps", "shadercache"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>A game's folder with nothing in it is not a cache with something in it.</summary>
    [Fact]
    public async Task AGameFolderHoldingOnlyEmptyFoldersIsNoRow()
    {
        var install = RegisterInstall();
        Directory.CreateDirectory(Path.Combine(install, "steamapps", "shadercache", "440", "fozpipelinesv6"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// An unrecognised child with something in it is not presence either: it is never offered, so a
    /// row for it would offer nothing.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedChildAloneIsNoRow()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(install, "steamapps", "shadercache", "backup"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A game's cache moved to another drive with a link is the one a user most wants an answer
    /// about. It is never followed, so it is presence for the sentence the plan owes about it, not
    /// for a deletion.
    /// </summary>
    [Fact]
    public async Task AGameFolderThatIsALinkIsPresenceAndIsSaidToBeLeftAlone()
    {
        var install = RegisterInstall();
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere", "570"));
        var link = Path.Combine(install, "steamapps", "shadercache", "570");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(link, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A game's folder Windows will not list may hold anything, so it is presence: a refusal is never
    /// read as an empty folder, or the one cache on the machine could go without a row.
    /// </summary>
    [Fact]
    public async Task AGameFolderWindowsWillNotListIsPresence()
    {
        var install = RegisterInstall();
        var cache = CacheFor(install, "570");

        using var denied = new DeniedDirectory(cache);

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A list naming a library Deguffer cannot place is said out loud, and the row is never called
    /// clear: that library was never looked at.
    /// </summary>
    [Fact]
    public async Task ALibraryTheListNamesWithoutAFullPathIsSaidAndTheRowIsNeverClear()
    {
        RegisterInstall();
        WriteList("\"libraryfolders\" { \"1\" { \"path\" \"SteamLibrary\" } }");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("names a library without a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every library the list names, the one beside the program included, and at Tier 2: removing
    /// these costs a download from Valve, never a local rebuild.
    /// </summary>
    [Fact]
    public async Task PlansEveryGamesCacheInEveryLibraryTheListNames()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);

        var first = CacheFor(install, "440");
        var second = CacheFor(SecondLibrary, "570");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(
            new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// A cache in a library other than the one beside the program is presence on its own. Without
    /// the list, this machine's largest cache would never produce a row.
    /// </summary>
    [Fact]
    public async Task ACacheInASecondLibraryAloneIsPresence()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);
        CacheFor(SecondLibrary, "570");

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The layout clients wrote before the per-library blocks: each library's path is its numbered
    /// entry's own value, beside settings that are not libraries.
    /// </summary>
    [Fact]
    public async Task ReadsTheOlderLayoutOfTheList()
    {
        var install = RegisterInstall();
        WriteList($$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport"		"1600000000"
                "ContentStatsID"		"-1234567890"
                "1"		"{{SecondLibrary.Replace(@"\", @"\\")}}"
            }
            """);

        var second = CacheFor(SecondLibrary, "570");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(second, Assert.Single(plan.TargetedPaths));
    }

    /// <summary>
    /// A list Deguffer could not make sense of. The library beside the program is still examined,
    /// and the plan says the rest were neither cleared nor ruled out — rather than presenting one
    /// library's figure as the machine's.
    /// </summary>
    [Fact]
    public async Task AListThatCannotBeUnderstoodIsSaidAndTheInstallLibraryIsStillExamined()
    {
        var install = RegisterInstall();
        WriteList("\"libraryfolders\"\n{\n    \"1\"\n    {\n        \"path\"   \"D:\\\\SteamLibrary\n");
        var first = CacheFor(install, "440");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(first, Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not make sense of Steam's list", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same list with no cache anywhere is still a row, and not one that reads "Already clear":
    /// the libraries it names were never looked at.
    /// </summary>
    [Fact]
    public async Task AListThatCannotBeUnderstoodIsARowThatIsNeverCalledClear()
    {
        RegisterInstall();
        WriteList("not a list");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// A list Windows would not let Deguffer read — here because Steam holds it open exclusively — is
    /// its own warning and its own flag, and never read as "there is only one library".
    /// </summary>
    [Fact]
    public async Task AListWindowsWillNotLetDegufferReadIsSaidToBeUnreadable()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);
        CacheFor(SecondLibrary, "570");

        using var held = new FileStream(
            Path.Combine(install, "steamapps", "libraryfolders.vdf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not read Steam's list", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2's dangerous direction inside the container: a child whose name is not an application id
    /// is not a target, is named on the plan, and is still there after the run.
    /// </summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("440.old")]
    [InlineData("-440")]
    [InlineData("99999999999")]
    public async Task AChildThatIsNotAnApplicationIdIsLeftAloneAndNamed(string name)
    {
        var install = RegisterInstall();
        CacheFor(install, "440");
        var stranger = Populate(Path.Combine(install, "steamapps", "shadercache", name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(stranger, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(stranger), $"{stranger} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6's negative, in both libraries: the games, the half-finished download, the Workshop
    /// content, Steam's list of libraries and each game's manifest all survive a run that removed
    /// every cache, each asserted by name rather than by an assertion on <c>steamapps</c>.
    /// </summary>
    [Fact]
    public async Task TheGamesTheDownloadAndSteamsRecordsAllSurvive()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);

        var caches = new[] { CacheFor(install, "440"), CacheFor(SecondLibrary, "570") };

        var directories = new List<string>();
        var files = new List<string> { Path.Combine(install, "steamapps", "libraryfolders.vdf") };

        foreach (var library in new[] { install, SecondLibrary })
        {
            directories.AddRange(
            [
                Populate(Path.Combine(library, "steamapps", "common", "Some Game")),
                Populate(Path.Combine(library, "steamapps", "downloading", "570")),
                Populate(Path.Combine(library, "steamapps", "temp", "570")),
                Populate(Path.Combine(library, "steamapps", "workshop", "content")),
                Populate(Path.Combine(library, "steamapps", "sourcemods", "SomeMod")),
            ]);
        }

        files.Add(Manifest(install, "440", "Team Fortress 2"));
        files.Add(Manifest(SecondLibrary, "570", "Dota 2"));

        var marker = Path.Combine(SecondLibrary, "libraryfolder.vdf");
        File.WriteAllText(marker, "\"libraryfolder\" { }");
        files.Add(marker);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(caches.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        string[] containers =
        [
            install,
            SecondLibrary,
            Path.Combine(install, "steamapps"),
            Path.Combine(SecondLibrary, "steamapps"),
            Path.Combine(install, "steamapps", "shadercache"),
            Path.Combine(SecondLibrary, "steamapps", "shadercache"),
        ];

        // Each named neighbour itself, not only something inside it: the disk check below passes for
        // anything the run did not target, so only this says the plan asserts them.
        string[] neighbours =
        [
            .. new[] { install, SecondLibrary }.SelectMany(library =>
                new[] { "common", "downloading", "temp", "workshop", "sourcemods" }
                    .Select(name => Path.Combine(library, "steamapps", name))),
        ];

        foreach (var path in directories.Concat(files).Concat(containers))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var path in files.Concat(containers).Concat(neighbours))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(caches, c => Assert.False(Directory.Exists(c), $"{c} was not removed"));
        Assert.All(directories.Concat(containers), d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.All(files, f => Assert.True(File.Exists(f), $"{f} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3: the client downloads and processes these caches while it runs, so a plan made with Steam
    /// open says so. Both of its processes count, as they do for the web cache.
    /// </summary>
    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    public async Task APlanMadeWhileSteamRunsSaysSo(string process)
    {
        var install = RegisterInstall();
        CacheFor(install, "440");

        var provider = new SteamShaderCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A row a reader can choose from: each game named as Steam names it, and whether it is still
    /// installed, because a cache for a game that is gone costs nothing to remove.
    /// </summary>
    [Fact]
    public async Task EachGameIsNamedFromItsManifestAndSaysWhetherItIsInstalled()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);
        CacheFor(install, "440");
        CacheFor(install, "730");

        // Installed in the other library, which is where its manifest is.
        Manifest(SecondLibrary, "440", "Team Fortress 2");

        var steps = (await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>().ToList();

        var installed = Assert.Single(steps, s => s.Identity?.Key == "440");
        Assert.Equal("Team Fortress 2", installed.Identity!.Name);
        Assert.Contains("Team Fortress 2", installed.What, StringComparison.Ordinal);
        Assert.Contains(new ItemFacet("Installed", "Yes"), installed.Facets);

        var gone = Assert.Single(steps, s => s.Identity?.Key == "730");
        Assert.Equal("Steam app 730", gone.Identity!.Name);
        Assert.Contains(new ItemFacet("Installed", "No"), gone.Facets);
    }

    /// <summary>
    /// "Not installed" is a claim about every library, so it is not made where the list could not be
    /// read and a library it names may hold the game.
    /// </summary>
    [Fact]
    public async Task NoGameIsCalledUninstalledWhenTheListCouldNotBeRead()
    {
        var install = RegisterInstall();
        WriteList("not a list");
        CacheFor(install, "730");

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.DoesNotContain(step.Facets, f => f.Label == "Installed");
    }

    /// <summary>
    /// A <c>steamapps</c> moved onto another drive with a link. Nothing is removed through it, and
    /// the plan says so rather than deleting the far side of a redirection Steam's record never named.
    /// </summary>
    [Fact]
    public async Task ALinkedSteamappsIsNeverLookedThrough()
    {
        var install = RegisterInstall();
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var farCache = CacheFor(outside, "440");
        Directory.CreateSymbolicLink(Path.Combine(install, "steamapps"), Path.Combine(outside, "steamapps"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(farCache), $"{farCache} was removed through the link");
    }

    /// <summary>A game's folder that is itself a link is named and never followed.</summary>
    [Fact]
    public async Task ALinkedGameFolderIsNeverFollowed()
    {
        var install = RegisterInstall();
        CacheFor(install, "440");
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere", "570"));
        var link = Path.Combine(install, "steamapps", "shadercache", "570");
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(link, StringComparison.OrdinalIgnoreCase));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
    }

    /// <summary>
    /// A library Windows will not describe is named as unreached, and the others are still planned.
    /// </summary>
    [Fact]
    public async Task ALibraryWindowsWillNotDescribeIsNamedAndTheRestArePlanned()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);
        var first = CacheFor(install, "440");
        CacheFor(SecondLibrary, "570");

        using var denied = DeniedDirectory.WithUnreadableAttributes(SecondLibrary);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(first, Assert.Single(plan.TargetedPaths));
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(SecondLibrary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Steam in the profile and no record of where it is installed. The list of libraries lives in
    /// the install, so nothing was looked at, and the row says that rather than "Already clear".
    /// </summary>
    [Fact]
    public async Task ASteamWhoseInstallCannotBeFoundIsPresentAndUnexamined()
    {
        Directory.CreateDirectory(LocalRoot);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("shader cache in Steam's game libraries", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1: Explore reads the same rule. It refuses <c>steamapps</c>, everything beside the
    /// container, the container itself and an unrecognised child, and allows a game's cache, in
    /// every library the list names.
    /// </summary>
    [Fact]
    public void ExploreRefusesEverythingInALibraryButAGamesCache()
    {
        var install = RegisterInstall();
        WriteLibraryList(install, SecondLibrary);

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        foreach (var library in new[] { install, SecondLibrary })
        {
            var steamapps = Path.Combine(library, "steamapps");

            Assert.False(policy.MayRemove(steamapps).IsAllowed);
            Assert.False(policy.MayRemove(Path.Combine(steamapps, "common")).IsAllowed);
            Assert.False(policy.MayRemove(Path.Combine(steamapps, "common", "Some Game")).IsAllowed);
            Assert.False(policy.MayRemove(Path.Combine(steamapps, "downloading")).IsAllowed);
            Assert.False(policy.MayRemove(Path.Combine(steamapps, "shadercache")).IsAllowed);
            Assert.False(policy.MayRemove(Path.Combine(steamapps, "shadercache", "backup")).IsAllowed);
            Assert.True(policy.MayRemove(Path.Combine(steamapps, "shadercache", "440")).IsAllowed);
        }
    }

    /// <summary>
    /// G4: Steam's list is read once per planning pass however many questions are put to the
    /// provider, and again after an invalidation, so a library added while the app was open is seen.
    /// </summary>
    [Fact]
    public async Task TheListIsReadOncePerPassAndAgainAfterInvalidation()
    {
        var install = RegisterInstall();
        WriteLibraryList(install);
        CacheFor(install, "440");

        var provider = CreateProvider();
        await provider.IsPresentAsync();
        await provider.PlanAsync();
        Assert.Equal(2, provider.ToolRoots.Count);

        WriteLibraryList(install, SecondLibrary);
        CacheFor(SecondLibrary, "570");

        Assert.Single((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal(2, (await provider.PlanAsync()).TargetedPaths.Count);
        Assert.Equal(4, provider.ToolRoots.Count);
    }
}
