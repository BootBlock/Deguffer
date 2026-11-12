using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a Godot project's data folder is, read from its <c>project.godot</c>. The name decides which
/// folder the Godot rows look inside, so every way the file could leave it uncertain must give no
/// answer rather than the default.
/// </summary>
public sealed class GodotProjectSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AGodot4ProjectKeepsItsDataInTheHiddenFolderByDefault()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project);

        Assert.Equal(new GodotProjectSettings(5, ".godot"), GodotProjectSettings.Read(project));
    }

    /// <summary>
    /// The setting is read in its section, after a description whose text holds a section header on a
    /// line of its own. A reader that took that line as a header would read the setting as another
    /// section's and answer <c>.godot</c>.
    /// </summary>
    [Fact]
    public void TheSettingTurningTheHiddenFolderOffIsReadInItsSection()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, hiddenDataFolder: false);

        Assert.Equal(new GodotProjectSettings(5, "godot"), GodotProjectSettings.Read(project));
    }

    [Theory]
    [InlineData(4, true, ".import")]
    [InlineData(3, true, ".import")]
    [InlineData(4, false, "import")]
    public void AGodot3ProjectKeepsItsImportCacheAsItsDataFolder(int configVersion, bool hidden, string expected)
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, hiddenDataFolder: hidden, configVersion: configVersion);

        var settings = GodotProjectSettings.Read(project);

        Assert.Equal(new GodotProjectSettings(configVersion, expected), settings);
        Assert.False(settings!.IsGodot4);
    }

    /// <summary>
    /// A setting with a feature tag applies on some machines and not others, and a value the engine
    /// would not parse is not one it wrote.
    /// </summary>
    [Theory]
    [InlineData("config/use_hidden_project_data_directory.windows=false")]
    [InlineData("config/use_hidden_project_data_directory=maybe")]
    public void ASettingTheReaderCannotApplyGivesNoAnswer(string line)
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, settings: [line]);

        Assert.Null(GodotProjectSettings.Read(project));
    }

    /// <summary>
    /// A settings-override file in the project is read as <c>override.cfg</c> is, under every variant
    /// of the setting that names it, and one that names the data-folder setting gives no answer.
    /// </summary>
    [Theory]
    [InlineData("config/project_settings_override")]
    [InlineData("config/project_settings_override.windows")]
    public void AnOverrideFileInTheProjectNamingTheSettingGivesNoAnswer(string key)
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, settings: [$"{key}=\"res://settings/local.cfg\""]);
        Directory.CreateDirectory(Path.Combine(project, "settings"));

        File.WriteAllText(Path.Combine(project, "settings", "local.cfg"), "[display]\nwindow/size/mode=2\n");
        Assert.Equal(".godot", GodotProjectSettings.Read(project)?.DataFolderName);

        File.WriteAllText(Path.Combine(project, "settings", "local.cfg"), "[application]\nconfig/use_hidden_project_data_directory=false\n");
        Assert.Null(GodotProjectSettings.Read(project));
    }

    /// <summary>
    /// A settings-override file outside the project cannot be read from it, and projects keep
    /// per-user settings there. The name then rests on <c>project.godot</c>, and the content rules
    /// decide what is offered.
    /// </summary>
    [Fact]
    public void AnOverrideFileOutsideTheProjectIsNotRead()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, settings: ["config/project_settings_override=\"user://engine_settings.cfg\""]);

        Assert.Equal(".godot", GodotProjectSettings.Read(project)?.DataFolderName);
    }

    [Fact]
    public void AnEmptyOverrideFileSettingLeavesTheNameAlone()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project, settings: ["config/project_settings_override=\"\""]);

        Assert.Equal(".godot", GodotProjectSettings.Read(project)?.DataFolderName);
    }

    /// <summary>The engine applies an <c>override.cfg</c> beside the project after <c>project.godot</c>.</summary>
    [Fact]
    public void AnOverrideFileNamingTheSettingGivesNoAnswer()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project);

        File.WriteAllText(Path.Combine(project, "override.cfg"), "[display]\nwindow/size/mode=2\n");
        Assert.Equal(".godot", GodotProjectSettings.Read(project)?.DataFolderName);

        File.WriteAllText(Path.Combine(project, "override.cfg"), "[application]\nconfig/use_hidden_project_data_directory=true\n");
        Assert.Null(GodotProjectSettings.Read(project));
    }

    /// <summary>The engine reads a <c>project.binary</c> in preference to <c>project.godot</c>.</summary>
    [Fact]
    public void ABinarySettingsFileGivesNoAnswer()
    {
        var project = Project();
        GodotProjectFixture.CreateProject(project);
        File.WriteAllBytes(Path.Combine(project, "project.binary"), [0x45, 0x43, 0x46, 0x47]);

        Assert.Null(GodotProjectSettings.Read(project));
    }

    [Theory]
    [InlineData("config/name=\"Example\"\n")]
    [InlineData("config_version=five\n")]
    [InlineData("config_version=5\n[application\n")]
    [InlineData("config_version=5\nconfig/name=\"open\n")]
    [InlineData("config_version=5\njump=}\n")]
    [InlineData("config_version=5\nnot a setting\n")]
    public void AFileTheEngineWouldNotHaveWrittenGivesNoAnswer(string text)
    {
        var project = Project();
        File.WriteAllText(Path.Combine(project, "project.godot"), text);

        Assert.Null(GodotProjectSettings.Read(project));
    }

    [Fact]
    public void NoProjectFileGivesNoAnswer() => Assert.Null(GodotProjectSettings.Read(Project()));

    private string Project() => _temp.CreateDirectory("Game");
}
