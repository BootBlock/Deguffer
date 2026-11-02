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
    /// </summary>
    private FakeProcessRunner CliAnswering(
        Dictionary<string, string?>? overrides = null,
        Dictionary<string, string?>? ide = null,
        List<string>? ran = null)
    {
        _environment.WithExecutable("arduino-cli");

        Dictionary<string, string?> Settings(Dictionary<string, string?>? changes)
        {
            var settings = new Dictionary<string, string?>
            {
                ["directories.data"] = Data,
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

        var own = Settings(overrides);
        var ides = Settings(ide);
        var ideArgument = $" --config-file \"{IdeConfig}\"";

        return new FakeProcessRunner().Replying(Cli, arguments =>
        {
            ran?.Add(arguments);

            var settings = arguments.EndsWith(ideArgument, StringComparison.Ordinal) ? ides : own;
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
}
