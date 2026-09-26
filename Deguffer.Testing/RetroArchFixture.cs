using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// A copy of RetroArch as its Windows build lays itself out: everything beside <c>retroarch.exe</c>,
/// with the settings file RetroArch writes there. Each part is created only when a test asks, so a test
/// states what it needs.
/// </summary>
public sealed class RetroArchFixture(FakeUserEnvironment environment, string program)
{
    public string Program { get; } = program;

    public string Shaders => Path.Combine(Program, "shaders");

    public string Database => Path.Combine(Program, "database", "rdb");

    public string Thumbnails => Path.Combine(Program, "thumbnails");

    public string Settings => Path.Combine(Program, RetroArchInstall.SettingsFileName);

    public string ApplicationDataSettings => Path.Combine(environment.RoamingAppData, RetroArchInstall.SettingsFileName);

    /// <summary>The program, and the settings file RetroArch writes beside it, with the lines given.</summary>
    public RetroArchFixture Install(params string[] settings)
    {
        WriteFile(Path.Combine(Program, "retroarch.exe"), 64);
        File.WriteAllText(Settings, string.Join("\n", settings) + "\n");
        return this;
    }

    /// <summary>The program with no settings file beside it.</summary>
    public RetroArchFixture InstallProgramOnly()
    {
        WriteFile(Path.Combine(Program, "retroarch.exe"), 64);
        return this;
    }

    public void WriteApplicationDataSettings(params string[] settings)
    {
        Directory.CreateDirectory(environment.RoamingAppData);
        File.WriteAllText(ApplicationDataSettings, string.Join("\n", settings) + "\n");
    }

    /// <summary>Tell Deguffer the program's folder under Emulator folders.</summary>
    public RetroArchFixture Declare()
    {
        Assert.True(new EmulatorFolderStore(environment).Save([Program]));
        return this;
    }

    /// <summary>A shader set as the updater extracts it.</summary>
    public string ShaderSet(string name, string? shaders = null) =>
        Folder(Path.Combine(shaders ?? Shaders, name));

    public string DatabaseFile(string system, string? database = null) =>
        WriteFile(Path.Combine(database ?? Database, system + ".rdb"));

    /// <summary>One kind of picture for one system, as RetroArch writes it.</summary>
    public string Pictures(string system, string kind, string? thumbnails = null) =>
        Folder(Path.Combine(thumbnails ?? Thumbnails, system, kind));

    public RetroArchDiscovery Discovery(ISystemDirectories system, SteamDiscovery? steam = null) =>
        new(environment, steam, system: system);

    public static string WriteFile(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public static string Folder(string path)
    {
        WriteFile(Path.Combine(path, "entry.bin"));
        return path;
    }
}
