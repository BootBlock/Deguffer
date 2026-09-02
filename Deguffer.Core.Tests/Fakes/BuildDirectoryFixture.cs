using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Builds the four project layouts the build-directory providers recognise, on disk.
///
/// Every marker is separately defeatable, because the interesting cases are the ones where a piece
/// of evidence is missing: that is precisely where a rule which recognises by directory name rather
/// than by the project around it gets it wrong. Paths are synthetic throughout; nothing here is
/// copied from a real machine, and no toolchain needs to be installed for any of it.
/// </summary>
public static class BuildDirectoryFixture
{
    /// <summary>
    /// A Unity project, returning its <c>Library</c>. Omitting a marker reproduces a directory
    /// called <c>Library</c> that is not Unity's.
    /// </summary>
    public static string CreateUnityProject(
        string projectDirectory,
        bool writeAssets = true,
        bool writePackages = true,
        bool writeProjectSettings = true,
        bool writeLockfile = false,
        int payloadBytes = 4096)
    {
        if (writeAssets)
        {
            Directory(projectDirectory, "Assets");
            WriteText(Path.Combine(projectDirectory, "Assets", "Player.cs"), "// the user's own source");
        }

        if (writePackages)
        {
            Directory(projectDirectory, "Packages");
            WriteText(Path.Combine(projectDirectory, "Packages", "manifest.json"), "{}");
        }

        if (writeProjectSettings)
        {
            Directory(projectDirectory, "ProjectSettings");
        }

        var library = Directory(projectDirectory, "Library");
        Directory(library, "ShaderCache");
        WriteBytes(Path.Combine(library, "ShaderCache", "shaders.bin"), payloadBytes);
        WriteBytes(Path.Combine(library, "ArtifactDB"), payloadBytes);

        if (writeLockfile)
        {
            WriteText(Path.Combine(library, "UnityLockfile"), string.Empty);
        }

        return library;
    }

    /// <summary>
    /// A Cargo project, returning its <c>target</c>. <paramref name="writeCacheTag"/> false is the
    /// directory Cargo never wrote, which must not be recognised on the manifest alone.
    /// </summary>
    public static string CreateCargoProject(
        string projectDirectory,
        bool writeManifest = true,
        bool writeCacheTag = true,
        int payloadBytes = 4096)
    {
        if (writeManifest)
        {
            WriteText(Path.Combine(projectDirectory, "Cargo.toml"), "[package]\nname = \"example\"");
        }

        Directory(projectDirectory, "src");
        WriteText(Path.Combine(projectDirectory, "src", "main.rs"), "fn main() {}");

        var target = Directory(projectDirectory, "target");

        if (writeCacheTag)
        {
            WriteText(Path.Combine(target, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55");
        }

        Directory(target, "debug");
        WriteBytes(Path.Combine(target, "debug", "example.exe"), payloadBytes);

        return target;
    }

    /// <summary>
    /// A Node.js project, returning its <c>node_modules</c>. <paramref name="lockFile"/> null is the
    /// project with no lock file, whose dependencies are not reproducible and so not disposable.
    /// </summary>
    public static string CreateNodeProject(
        string projectDirectory,
        bool writeManifest = true,
        string? lockFile = "package-lock.json",
        int payloadBytes = 4096)
    {
        if (writeManifest)
        {
            WriteText(Path.Combine(projectDirectory, "package.json"), "{ \"name\": \"example\" }");
        }

        if (lockFile is not null)
        {
            WriteText(Path.Combine(projectDirectory, lockFile), "{}");
        }

        WriteText(Path.Combine(projectDirectory, "index.js"), "// the user's own source");

        var modules = Directory(projectDirectory, "node_modules");
        Directory(modules, "left-pad");
        WriteBytes(Path.Combine(modules, "left-pad", "index.js"), payloadBytes);

        return modules;
    }

    /// <summary>
    /// A Python project, returning its virtual environment. Both markers are defeatable: without
    /// <c>pyvenv.cfg</c> the directory is not an environment, and without a manifest it is not
    /// reproducible.
    /// </summary>
    public static string CreatePythonProject(
        string projectDirectory,
        string directoryName = ".venv",
        bool writeConfig = true,
        string? manifest = "requirements.txt",
        int payloadBytes = 4096)
    {
        if (manifest is not null)
        {
            WriteText(Path.Combine(projectDirectory, manifest), "requests==2.31.0");
        }

        WriteText(Path.Combine(projectDirectory, "main.py"), "# the user's own source");

        var environment = Directory(projectDirectory, directoryName);

        if (writeConfig)
        {
            WriteText(Path.Combine(environment, "pyvenv.cfg"), "include-system-site-packages = false");
        }

        Directory(environment, "Lib");
        WriteBytes(Path.Combine(environment, "Lib", "site.py"), payloadBytes);

        return environment;
    }

    /// <summary>A Dart project, returning its <c>build</c>.</summary>
    public static string CreateDartProject(
        string projectDirectory,
        bool writePubspec = true,
        bool writeDartTool = true,
        int payloadBytes = 4096)
    {
        if (writePubspec)
        {
            WriteText(Path.Combine(projectDirectory, "pubspec.yaml"), "name: example");
        }

        if (writeDartTool)
        {
            Directory(projectDirectory, ".dart_tool");
        }

        Directory(projectDirectory, "lib");
        WriteText(Path.Combine(projectDirectory, "lib", "main.dart"), "// the user's own source");

        var build = Directory(projectDirectory, "build");
        WriteBytes(Path.Combine(build, "app.apk"), payloadBytes);

        return build;
    }

    private static string Directory(params string[] segments)
    {
        var full = Path.Combine(segments);
        System.IO.Directory.CreateDirectory(LongPath.Extended(full));
        return full;
    }

    private static void WriteText(string path, string content)
    {
        System.IO.Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(path)!));
        File.WriteAllText(LongPath.Extended(path), content);
    }

    private static void WriteBytes(string path, int bytes)
    {
        System.IO.Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(path)!));
        File.WriteAllBytes(LongPath.Extended(path), new byte[bytes]);
    }
}
