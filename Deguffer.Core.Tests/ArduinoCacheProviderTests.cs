using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1 with a command that removes whatever folder its settings name, and a build cache read by the
/// marker the CLI writes. The fake CLI answers <c>config get --json</c> with one JSON string and
/// removes the downloads folder outright on <c>cache clean</c>, as arduino-cli's source does.
/// </summary>
public sealed class ArduinoCacheProviderTests : IDisposable
{
    /// <summary>The variable that moves the CLI's data folder, and with it the staging folder inside.</summary>
    private const string DataVariable = "ARDUINO_DIRECTORIES_DATA";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ArduinoCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Cli => _environment.FindExecutable("arduino-cli")!;

    private string Data => Path.Combine(_environment.LocalAppData, "Arduino15");

    private string Staging => Path.Combine(Data, "staging");

    private string BuildCache => Path.Combine(_environment.LocalAppData, "arduino");

    private string Sketchbook => Path.Combine(_environment.UserProfile, "Documents", "Arduino");

    private string IdeConfig => Path.Combine(_environment.UserProfile, ".arduinoIDE", "arduino-cli.yaml");

    private ArduinoCacheProvider CreateProvider(FakeProcessRunner runner) =>
        new(_environment, runner, FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>
    /// The CLI installed, answering each setting as <paramref name="overrides"/> says and otherwise as
    /// its defaults do, and removing the downloads folder of the configuration it was given on
    /// <c>cache clean</c>. <paramref name="ide"/> answers for <c>--config-file</c> naming the IDE's file.
    ///
    /// <para>The data folder follows <c>ARDUINO_DIRECTORIES_DATA</c> in the environment the CLI starts
    /// with, as the CLI's binding of its <c>ARDUINO_</c> variables does: Deguffer's own where the run
    /// names none, and the one it was handed where it names one.</para>
    /// </summary>
    private FakeProcessRunner CliAnswering(
        Dictionary<string, string?>? overrides = null,
        Dictionary<string, string?>? ide = null,
        List<string>? ran = null)
    {
        _environment.WithExecutable("arduino-cli");

        var ideArgument = $" --config-file \"{IdeConfig}\"";

        return new FakeProcessRunner().Replying(Cli, (arguments, run) =>
        {
            ran?.Add(arguments);

            var variable = run.Environment is { } started
                ? started.GetValueOrDefault(DataVariable)
                : _environment.GetToolEnvironmentVariable(DataVariable);
            var settings = Settings(arguments.EndsWith(ideArgument, StringComparison.Ordinal) ? ide : overrides, variable ?? Data);
            var bare = arguments.EndsWith(ideArgument, StringComparison.Ordinal) ? arguments[..^ideArgument.Length] : arguments;

            if (bare == "cache clean")
            {
                var downloads = settings["directories.downloads"] is { Length: > 0 } moved
                    ? moved
                    : Path.Combine(settings["directories.data"]!, "staging");

                if (Directory.Exists(downloads))
                {
                    Directory.Delete(downloads, recursive: true);
                }

                return new CommandOutcome(0, string.Empty, string.Empty);
            }

            Assert.StartsWith("config get ", bare, StringComparison.Ordinal);
            Assert.EndsWith(" --json", bare, StringComparison.Ordinal);

            var key = bare["config get ".Length..^" --json".Length];

            return settings[key] is { } value
                ? new CommandOutcome(0, JsonSerializer.Serialize(value) + "\n", string.Empty)
                : new CommandOutcome(1, string.Empty, $"Cannot get the configuration key {key}");
        });

        Dictionary<string, string?> Settings(Dictionary<string, string?>? changes, string data)
        {
            var settings = new Dictionary<string, string?>
            {
                ["directories.data"] = data,
                ["directories.downloads"] = string.Empty,
                ["build_cache.path"] = BuildCache,
                ["directories.user"] = Sketchbook,
            };

            foreach (var (key, value) in changes ?? [])
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    /// <summary>A data folder in the shape the CLI leaves it, with archives staged in both parts.</summary>
    private void InstalledData()
    {
        _temp.CreateFile(4096, "profile", "AppData", "Local", "Arduino15", "staging", "packages", "avr-gcc-7.3.0.tar.bz2");
        _temp.CreateFile(2048, "profile", "AppData", "Local", "Arduino15", "staging", "libraries", "Servo-1.2.2.zip");
        _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "packages", "arduino", "hardware", "avr", "boards.txt");
        _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "internal", "profile.txt");
        _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "package_index.json");
        _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "inventory.yaml");
        _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "arduino-cli.yaml");
        _temp.CreateFile(100, "profile", "Documents", "Arduino", "Blink", "Blink.ino");
    }

    private void Exists(string path) => Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} did not survive");

    /// <summary>
    /// The staging folder goes through the CLI's own command, and everything beside it in the data
    /// folder, and the sketchbook, is asserted to have survived.
    /// </summary>
    [Fact]
    public async Task ClearsTheDownloadsWithTheClisOwnCommandAndKeepsEverythingBesideThem()
    {
        InstalledData();

        var provider = CreateProvider(CliAnswering());
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Cli, step.FileName);
        Assert.Equal("cache clean", step.Arguments);
        Assert.Equal(4096 + 2048, step.Estimated.Logical);

        foreach (var survivor in new[] { Data, Path.Combine(Data, "packages"), Path.Combine(Data, "internal"), Path.Combine(Data, "package_index.json"), Path.Combine(Data, "inventory.yaml"), Path.Combine(Data, "arduino-cli.yaml"), Sketchbook })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(Staging, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(Staging));
        Exists(Path.Combine(Data, "packages", "arduino", "hardware", "avr", "boards.txt"));
        Exists(Path.Combine(Data, "package_index.json"));
        Exists(Path.Combine(Sketchbook, "Blink", "Blink.ino"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2: a file or a folder the CLI does not stage, which the command would remove with the rest.
    /// </summary>
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("backup", "old.zip")]
    public async Task DoesNotRunTheCommandWhereTheDownloadsHoldSomethingElse(params string[] stranger)
    {
        InstalledData();
        _temp.CreateFile(100, ["profile", "AppData", "Local", "Arduino15", "staging", .. stranger]);

        var ran = new List<string>();
        var provider = CreateProvider(CliAnswering(ran: ran));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.Notes, n => n.Message.Contains($"'{stranger[0]}'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Staging, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Exists(Path.Combine([Staging, .. stranger]));
        Assert.DoesNotContain(ran, a => a.StartsWith("cache clean", StringComparison.Ordinal));
    }

    /// <summary>
    /// A downloads setting naming the sketchbook would have the command remove the user's sketches.
    /// </summary>
    [Theory]
    [InlineData("sketchbook")]
    [InlineData("data")]
    public async Task DoesNotRunTheCommandWhereTheDownloadsAreAnotherArduinoFolder(string which)
    {
        InstalledData();

        var downloads = which == "sketchbook" ? Sketchbook : Data;
        var provider = CreateProvider(CliAnswering(new() { ["directories.downloads"] = downloads }));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.Notes, n => n.Message.Contains("removes that folder outright", StringComparison.Ordinal));
    }

    /// <summary>
    /// The build cache: entries the CLI marked are offered, and an entry without the marker, a stray
    /// file and an unrecognised folder beside the two parts all survive.
    /// </summary>
    [Fact]
    public async Task OffersOnlyTheBuildsTheCliMarkedAsItsOwn()
    {
        var core = Path.Combine(BuildCache, "cores", "arduino_avr_uno_0123456789abcdef");
        var sketch = Path.Combine(BuildCache, "sketches", "FEDCBA9876543210");
        var unmarked = Path.Combine(BuildCache, "sketches", "not-a-build");
        var stranger = Path.Combine(BuildCache, "projects");

        TempDirectory.Age(_temp.CreateFile(8192, "profile", "AppData", "Local", "arduino", "cores", "arduino_avr_uno_0123456789abcdef", "core.a"), TimeSpan.FromDays(40));
        TempDirectory.Age(_temp.CreateFile(0, "profile", "AppData", "Local", "arduino", "cores", "arduino_avr_uno_0123456789abcdef", ".last-used"), TimeSpan.FromDays(40));
        TempDirectory.Age(_temp.CreateFile(1024, "profile", "AppData", "Local", "arduino", "sketches", "FEDCBA9876543210", "Blink.ino.hex"), TimeSpan.FromDays(40));
        TempDirectory.Age(_temp.CreateFile(0, "profile", "AppData", "Local", "arduino", "sketches", "FEDCBA9876543210", ".last-used"), TimeSpan.FromDays(40));
        TempDirectory.Age(_temp.CreateFile(512, "profile", "AppData", "Local", "arduino", "sketches", "not-a-build", "mine.txt"), TimeSpan.FromDays(40));
        TempDirectory.Age(_temp.CreateFile(256, "profile", "AppData", "Local", "arduino", "projects", "keep.txt"), TimeSpan.FromDays(40));

        var provider = CreateProvider(CliAnswering());
        var plan = await provider.PlanAsync();

        Assert.Equal(
            [core, sketch],
            plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(8192 + 1024, plan.Steps.OfType<DeleteDirectoryStep>().Sum(s => s.Estimated.Logical));

        foreach (var survivor in new[] { BuildCache, unmarked, stranger })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(core));
        Assert.False(Directory.Exists(sketch));
        Exists(Path.Combine(unmarked, "mine.txt"));
        Exists(Path.Combine(stranger, "keep.txt"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Arduino IDE 2 hands its CLI a configuration of its own, which can keep the downloads elsewhere.
    /// That folder is asked about and cleared through the same file.
    /// </summary>
    [Fact]
    public async Task ClearsTheIdesOwnDownloadsThroughTheIdesConfiguration()
    {
        InstalledData();
        _temp.CreateFile(10, "profile", ".arduinoIDE", "arduino-cli.yaml");
        var ideDownloads = Path.Combine(_environment.LocalAppData, "ArduinoIdeDownloads");
        _temp.CreateFile(3000, "profile", "AppData", "Local", "ArduinoIdeDownloads", "packages", "esp32-3.0.0.zip");

        var provider = CreateProvider(CliAnswering(ide: new() { ["directories.downloads"] = ideDownloads }));
        var plan = await provider.PlanAsync();

        Assert.Equal(
            ["cache clean", $"cache clean --config-file \"{IdeConfig}\""],
            plan.Steps.OfType<RunCommandStep>().Select(s => s.Arguments));

        var result = await provider.ExecuteAsync(plan);

        Exists(IdeConfig);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(IdeConfig, StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(ideDownloads));
        Assert.False(Directory.Exists(Staging));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A CLI that will not say where its folders are leaves the row unexamined, not clear.</summary>
    [Fact]
    public async Task OffersNothingWhenTheCliDoesNotAnswer()
    {
        InstalledData();

        var provider = CreateProvider(CliAnswering(new() { ["directories.data"] = null }));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// §7.1: Explore may remove the staging folder and the build cache's parts, and nothing else in the
    /// data folder.
    /// </summary>
    [Fact]
    public async Task ExploreAllowsOnlyTheCachesInsideTheDataFolder()
    {
        InstalledData();

        var provider = CreateProvider(CliAnswering());
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.True(policy.MayRemove(Staging).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "packages")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "inventory.yaml")).IsAllowed);
    }

    /// <summary>
    /// §7.1 without the CLI: the default folders are still declared, so Explore may remove the staging
    /// folder and nothing else in the data folder.
    /// </summary>
    [Fact]
    public async Task ExploreKeepsTheDataFolderWhenNoCliIsInstalled()
    {
        InstalledData();

        var provider = CreateProvider(new FakeProcessRunner());
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.False(await provider.IsPresentAsync());
        Assert.True(policy.MayRemove(Staging).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "packages")).IsAllowed);
    }

    /// <summary>
    /// The CLI resolves its downloads folder afresh when <c>cache clean</c> starts, and removes it
    /// outright. A setting that moves it after the preview, through a variable or the configuration
    /// file, holds the command back, and the folder it would now reach is untouched.
    /// </summary>
    [Theory]
    [InlineData("variable")]
    [InlineData("file")]
    public async Task DoesNotRunTheCommandWhenTheSettingsChangedSinceThePreview(string how)
    {
        InstalledData();
        var elsewhere = _temp.CreateFile(100, "profile", "Documents", "Precious", "thesis.docx");

        var ran = new List<string>();
        var provider = CreateProvider(CliAnswering(ran: ran));
        var plan = await provider.PlanAsync();

        Assert.Single(plan.Steps.OfType<RunCommandStep>());

        if (how == "variable")
        {
            _environment.WithToolEnvironmentVariable("ARDUINO_DIRECTORIES_DOWNLOADS", Path.GetDirectoryName(elsewhere)!);
        }
        else
        {
            File.WriteAllText(Path.Combine(Data, "arduino-cli.yaml"), "directories:\n  downloads: somewhere-else\n");
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(ran, a => a.StartsWith("cache clean", StringComparison.Ordinal));
        Exists(elsewhere);
        Exists(Path.Combine(Staging, "packages", "avr-gcc-7.3.0.tar.bz2"));
        Assert.Contains(result.Steps, s => s.Message?.Contains("changed since the preview", StringComparison.Ordinal) == true);
    }

    /// <summary>A file saved into the downloads folder after the preview would go with the archives, so the command is held back.</summary>
    [Fact]
    public async Task DoesNotRunTheCommandWhenTheDownloadsGainedSomethingSinceThePreview()
    {
        InstalledData();

        var ran = new List<string>();
        var provider = CreateProvider(CliAnswering(ran: ran));
        var plan = await provider.PlanAsync();

        var saved = _temp.CreateFile(100, "profile", "AppData", "Local", "Arduino15", "staging", "my-library.zip");

        await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(ran, a => a.StartsWith("cache clean", StringComparison.Ordinal));
        Exists(saved);
    }

    /// <summary>
    /// A configuration file named by a setting that is not a full path cannot be read again before the
    /// clean, so the downloads are not offered.
    /// </summary>
    [Fact]
    public async Task LeavesTheDownloadsWhenItCannotTellWhichConfigurationTheCliReads()
    {
        InstalledData();
        _environment.WithToolEnvironmentVariable("ARDUINO_CONFIG_FILE", "arduino-cli.yaml");

        var provider = CreateProvider(CliAnswering());
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Staging, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The IDE's settings file is asked about even when Windows will not describe it, because a refusal
    /// is not an absence, and it survives the clean.
    /// </summary>
    [Fact]
    public async Task AsksAboutTheIdesConfigurationEvenWhenItCannotBeDescribed()
    {
        InstalledData();
        _temp.CreateFile(10, "profile", ".arduinoIDE", "arduino-cli.yaml");

        var ran = new List<string>();
        var provider = CreateProvider(CliAnswering(ran: ran));

        using (DeniedDirectory.WithUnreadableFile(IdeConfig))
        {
            await provider.PlanAsync();
        }

        Assert.Contains(ran, a => a.EndsWith($"--config-file \"{IdeConfig}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7.1 with folders the CLI moved: the moved downloads folder's two parts and the moved build
    /// cache's two parts may be removed in Explore, and what sits beside them may not.
    /// </summary>
    [Fact]
    public async Task ExploreFollowsTheFoldersTheCliMoved()
    {
        InstalledData();
        var downloads = Path.Combine(_environment.LocalAppData, "ArduinoDownloads");
        var cache = Path.Combine(_environment.LocalAppData, "ArduinoBuilds");
        _temp.CreateFile(10, "profile", "AppData", "Local", "ArduinoDownloads", "packages", "a.zip");
        _temp.CreateFile(10, "profile", "AppData", "Local", "ArduinoBuilds", "cores", "x", ".last-used");
        _temp.CreateFile(10, "profile", "AppData", "Local", "ArduinoBuilds", "notes.txt");

        var provider = CreateProvider(CliAnswering(new()
        {
            ["directories.downloads"] = downloads,
            ["build_cache.path"] = cache,
        }));
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.True(policy.MayRemove(Path.Combine(downloads, "packages")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(cache, "cores")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(cache, "notes.txt")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Data, "staging")).IsAllowed);
    }

    /// <summary>An entry is dated by the CLI's marker, which the CLI rewrites each time it uses the entry.</summary>
    [Fact]
    public async Task DatesEachBuildByTheClisMarker()
    {
        TempDirectory.Age(_temp.CreateFile(100, "profile", "AppData", "Local", "arduino", "sketches", "ABC", "Blink.ino.hex"), TimeSpan.FromDays(90));
        var marker = TempDirectory.Age(_temp.CreateFile(0, "profile", "AppData", "Local", "arduino", "sketches", "ABC", ".last-used"), TimeSpan.FromDays(12));

        var provider = CreateProvider(CliAnswering());
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Equal(File.GetLastWriteTimeUtc(marker), step.LastWritten);
    }

    /// <summary>
    /// With no CLI on the PATH, the one Arduino IDE 2 installs with itself answers, even where Windows
    /// will not describe it: a refusal is not an absence, so the row is still there.
    /// </summary>
    [Fact]
    public async Task FindsTheIdesBundledCliEvenWhenItCannotBeDescribed()
    {
        var bundled = _temp.CreateFile(
            10, "profile", "AppData", "Local", "Programs", "Arduino IDE", "resources", "app", "lib", "backend", "resources", "arduino-cli.exe");
        var asked = new List<string>();
        var provider = CreateProvider(new FakeProcessRunner().Replying(bundled, arguments =>
        {
            asked.Add(arguments);
            return new CommandOutcome(1, string.Empty, "unreachable");
        }));

        using (DeniedDirectory.WithUnreadableFile(bundled))
        {
            Assert.True(await provider.IsPresentAsync());
        }

        var plan = await provider.PlanAsync();

        Assert.NotEmpty(asked);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// A configuration edited while the CLI answers may have given answers from two configurations,
    /// so the plan holds nothing to it and offers no clean.
    /// </summary>
    [Fact]
    public async Task OffersNoCleanWhenTheConfigurationChangedWhileTheCliAnswered()
    {
        InstalledData();
        var config = Path.Combine(Data, "arduino-cli.yaml");
        var answer = CliAnswering();
        var provider = CreateProvider(new FakeProcessRunner().Replying(Cli, arguments =>
        {
            if (arguments.Contains("directories.downloads", StringComparison.Ordinal))
            {
                File.AppendAllText(config, "# edited\n");
            }

            return answer.RunAsync(Cli, arguments, ToolRun.Query, CancellationToken.None).GetAwaiter().GetResult();
        }));

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.Notes, n => n.Message.Contains("changed while it was asked", StringComparison.Ordinal));
    }

    /// <summary>The data folder a CLI the user starts now uses, once a variable set since start-up moves it.</summary>
    private string MovedData => Path.Combine(_environment.LocalAppData, "ArduinoMoved");

    /// <summary>A data folder in the shape the CLI leaves it, where the variable moved it to.</summary>
    private void InstalledMovedData()
    {
        _temp.CreateFile(4096, "profile", "AppData", "Local", "ArduinoMoved", "staging", "packages", "esp32-3.0.0.zip");
        _temp.CreateFile(100, "profile", "AppData", "Local", "ArduinoMoved", "packages", "esp32", "hardware", "boards.txt");
        _temp.CreateFile(100, "profile", "AppData", "Local", "ArduinoMoved", "inventory.yaml");
    }

    /// <summary>
    /// A variable set on the machine after Deguffer started moves the data folder of a CLI the user
    /// starts now and not that of the clean, which still starts with Deguffer's environment. Explore and a
    /// duplicate search hear the moved folder as the CLI's, and §5.6 asserts it and its installed boards
    /// survive, while the clean removes only the staging folder it was always sent to.
    /// </summary>
    [Fact]
    public async Task DeclaresTheDataFolderACliStartedNowUsesAndCleansOnlyTheOneTheCleanUses()
    {
        InstalledData();
        InstalledMovedData();
        _environment.WithEnvironmentVariableSetSinceStart(DataVariable, MovedData);

        var runner = CliAnswering();
        var provider = CreateProvider(runner);
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.True(policy.MayRemove(Path.Combine(MovedData, "staging")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(MovedData, "packages")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(MovedData, "inventory.yaml")).IsAllowed);
        Assert.Contains(
            await provider.CleanedPlacesAsync(),
            place => string.Equals(place.Path, Path.Combine(MovedData, "staging"), StringComparison.OrdinalIgnoreCase));

        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal([Staging], step.MeasuredPaths);
        Assert.Contains(runner.Invocations, i => i.Run.Environment is not null);
        Assert.Contains(plan.ProtectedPaths, p => string.Equals(p.Path, MovedData, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => string.Equals(p.Path, Path.Combine(MovedData, "packages"), StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Null(runner.Invocations.Single(i => i.Arguments == "cache clean").Run.Environment);
        Assert.False(Directory.Exists(Staging));
        Exists(Path.Combine(MovedData, "staging", "packages", "esp32-3.0.0.zip"));
        Exists(Path.Combine(MovedData, "packages", "esp32", "hardware", "boards.txt"));
        Exists(Path.Combine(Data, "packages", "arduino", "hardware", "avr", "boards.txt"));
    }

    /// <summary>
    /// The clean removes its downloads folder outright, so it is declined where a CLI the user starts
    /// now keeps its data folder there, and the folder is left standing with everything in it.
    /// </summary>
    [Fact]
    public async Task DoesNotRunTheCommandWhereACliStartedNowKeepsItsDataInTheDownloads()
    {
        InstalledData();
        _environment.WithEnvironmentVariableSetSinceStart(DataVariable, Staging);

        var plan = await CreateProvider(CliAnswering()).PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(plan.Notes, n => n.Message.Contains("started now", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => string.Equals(p.Path, Staging, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The build cache is read only where the CLI's builds cannot be confused with anything else, so a
    /// CLI the user starts now that keeps its data folder there leaves it unread.
    /// </summary>
    [Fact]
    public async Task LeavesTheBuildCacheWhereACliStartedNowKeepsItsDataThere()
    {
        _temp.CreateFile(100, "profile", "AppData", "Local", "arduino", "sketches", "ABC", ".last-used");
        _environment.WithEnvironmentVariableSetSinceStart(DataVariable, BuildCache);

        var plan = await CreateProvider(CliAnswering()).PlanAsync();

        Assert.Empty(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Contains(plan.Notes, n => n.Message.Contains("build cache", StringComparison.Ordinal)
            && n.Message.Contains("started now", StringComparison.Ordinal));
    }

    /// <summary>
    /// Where a CLI started with the changed environment does not say where its folders are, nothing can
    /// be held against them, so neither the clean nor a build is offered, and the plan says why.
    /// </summary>
    [Fact]
    public async Task OffersNoCleanWhenACliStartedNowDoesNotAnswer()
    {
        InstalledData();
        _temp.CreateFile(100, "profile", "AppData", "Local", "arduino", "sketches", "ABC", ".last-used");
        _environment.WithEnvironmentVariableSetSinceStart(DataVariable, MovedData);

        var answer = CliAnswering();
        var provider = CreateProvider(new FakeProcessRunner().Replying(Cli, (arguments, run) =>
            run.Environment is null
                ? answer.RunAsync(Cli, arguments, run, CancellationToken.None).GetAwaiter().GetResult()
                : new CommandOutcome(1, string.Empty, "unreadable configuration")));

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Severity is PlanNoteSeverity.Warning && n.Message.Contains("after Deguffer started", StringComparison.Ordinal));
    }

    /// <summary>
    /// On a machine nobody changed since Deguffer started, a CLI started now would answer as the one
    /// Deguffer starts, so each configuration is asked once.
    /// </summary>
    [Fact]
    public async Task AsksEachConfigurationOnceWhereNothingChangedSinceStartUp()
    {
        InstalledData();
        var runner = CliAnswering();

        await CreateProvider(runner).PlanAsync();

        Assert.Equal(4, runner.Invocations.Count);
        Assert.All(runner.Invocations, i => Assert.Null(i.Run.Environment));
    }
}
