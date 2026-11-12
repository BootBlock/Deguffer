using static Deguffer.Testing.BuildDirectoryFixture;

namespace Deguffer.Testing;

/// <summary>
/// Godot projects as the editor leaves them, on disk, for the three Godot rows. Names, hashes and
/// settings are synthetic, shaped as the engine's source says it writes them.
/// </summary>
public static class GodotProjectFixture
{
    /// <summary>The name of the one imported asset's files, as the importer bases them.</summary>
    public const string ImportedBase = "icon.svg-218a8f2b3041327d8a5756f3a245f83b";

    /// <summary>The shader group folder the fixture's one shader variant is in.</summary>
    public static readonly string ShaderGroup = new('a', 64);

    /// <summary>The files and folders in a Godot 4 data folder that no row offers, relative to it.</summary>
    public static readonly IReadOnlyList<string> DataFolderSurvivors =
    [
        "editor",
        "export_credentials.cfg",
        "uid_cache.bin",
        "global_script_class_cache.cfg",
        "mono",
        ".gdignore",
    ];

    /// <summary>The project's own files, relative to the project, which no row offers.</summary>
    public static readonly IReadOnlyList<string> ProjectSurvivors =
    [
        "project.godot",
        "icon.svg",
        "icon.svg.import",
        Path.Combine("scenes", "main.tscn"),
        Path.Combine("textures", "wall.png"),
        Path.Combine("textures", "wall.png.import"),
    ];

    /// <summary>
    /// A Godot 4 project, returning its data folder. The data folder holds an import cache and a
    /// shader cache in the engine's shapes, and everything else the editor keeps there, with
    /// something in each so §5.6 has content to assert.
    /// </summary>
    /// <param name="hiddenDataFolder">
    /// False writes <c>config/use_hidden_project_data_directory=false</c>, so the data folder is
    /// <c>godot</c>.
    /// </param>
    /// <param name="dataFolderName">
    /// The name the folder is given on disk, where a test wants it to disagree with the settings. Null
    /// gives it the name the settings choose.
    /// </param>
    /// <param name="settings">Extra lines for the <c>[application]</c> section.</param>
    public static string CreateProject(
        string projectDirectory,
        bool hiddenDataFolder = true,
        string? dataFolderName = null,
        int configVersion = 5,
        IReadOnlyList<string>? settings = null,
        int payloadBytes = 4096)
    {
        WriteProjectFile(projectDirectory, configVersion, hiddenDataFolder, settings);
        WriteAssets(projectDirectory);

        var data = Directory(projectDirectory, dataFolderName ?? (hiddenDataFolder ? ".godot" : "godot"));

        WriteText(Path.Combine(data, ".gdignore"), string.Empty);
        WriteBytes(Path.Combine(data, "editor", "filesystem_cache10"), 256);
        WriteText(Path.Combine(data, "export_credentials.cfg"), "[preset.0]\nscript_encryption_key=\"<YOUR_KEY>\"");
        WriteBytes(Path.Combine(data, "uid_cache.bin"), 64);
        WriteText(Path.Combine(data, "global_script_class_cache.cfg"), "list=[]");
        WriteBytes(Path.Combine(data, "mono", "temp", "bin", "Game.dll"), 256);

        WriteImportedAsset(Path.Combine(data, "imported"), payloadBytes);
        WriteShaderVariant(Path.Combine(data, "shader_cache"), $"{new string('b', 40)}.vulkan.cache", payloadBytes);

        return data;
    }

    /// <summary>A Godot 3 project, returning its <c>.import</c> folder.</summary>
    public static string CreateGodot3Project(string projectDirectory, int configVersion = 4, int payloadBytes = 4096)
    {
        WriteProjectFile(projectDirectory, configVersion, hiddenDataFolder: true, settings: null);
        WriteAssets(projectDirectory);

        var import = Directory(projectDirectory, ".import");
        WriteText(Path.Combine(import, ".gdignore"), string.Empty);
        WriteImportedAsset(import, payloadBytes);

        return import;
    }

    /// <summary>One imported asset: its product and the <c>.md5</c> the importer writes beside it.</summary>
    public static void WriteImportedAsset(string importCache, int payloadBytes = 4096, string importedBase = ImportedBase)
    {
        WriteBytes(Path.Combine(importCache, $"{importedBase}.ctex"), payloadBytes);
        WriteText(Path.Combine(importCache, $"{importedBase}.md5"), "source_md5=\"0\"\ndest_md5=\"0\"");
    }

    /// <summary>One shader variant file, in the fixture's shader and group.</summary>
    public static string WriteShaderVariant(string shaderCache, string fileName, int payloadBytes = 4096)
    {
        var path = Path.Combine(shaderCache, "CanvasShaderRD", ShaderGroup, fileName);
        WriteBytes(path, payloadBytes);
        return path;
    }

    /// <summary>
    /// A <c>project.godot</c> as the editor writes one, with a multi-line input map, so a reader that
    /// treats every line as a key reads the dictionary's lines as settings.
    /// </summary>
    private static void WriteProjectFile(string projectDirectory, int configVersion, bool hiddenDataFolder, IReadOnlyList<string>? settings)
    {
        var lines = new List<string>
        {
            "; Engine configuration file.",
            "; It's best edited using the editor UI and not directly,",
            "; since the parameters that go here are not all obvious.",
            ";",
            "; Format:",
            ";   [section] ; section goes between []",
            ";   param=value ; assign values to parameters",
            string.Empty,
            $"config_version={configVersion}",
            string.Empty,
            "[application]",
            string.Empty,
            "config/name=\"Example\"",
            "config/description=\"A line,",
            "[rendering]",
            "and another.\"",
        };

        if (!hiddenDataFolder)
        {
            lines.Add("config/use_hidden_project_data_directory=false");
        }

        lines.AddRange(settings ?? []);
        lines.AddRange(
        [
            string.Empty,
            "[input]",
            string.Empty,
            "jump={",
            "\"deadzone\": 0.5,",
            "\"events\": []",
            "}",
        ]);

        WriteText(Path.Combine(projectDirectory, "project.godot"), string.Join("\n", lines) + "\n");
    }

    private static void WriteAssets(string projectDirectory)
    {
        WriteText(Path.Combine(projectDirectory, "icon.svg"), "<svg/>");
        WriteText(Path.Combine(projectDirectory, "icon.svg.import"), "[remap]\nimporter=\"texture\"");
        WriteText(Path.Combine(projectDirectory, "scenes", "main.tscn"), "[gd_scene format=3]");
        WriteBytes(Path.Combine(projectDirectory, "textures", "wall.png"), 128);
        WriteText(Path.Combine(projectDirectory, "textures", "wall.png.import"), "[remap]\nimporter=\"texture\"");
    }
}
