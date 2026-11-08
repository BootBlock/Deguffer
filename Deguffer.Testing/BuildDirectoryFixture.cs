using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Builds the project layouts the build-directory providers recognise, on disk.
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

        return library;
    }

    /// <summary>
    /// A Cargo project, returning its <c>target</c>. <paramref name="writeCacheTag"/> false is the
    /// directory Cargo never wrote, which must not be recognised on the manifest alone.
    ///
    /// <para>The <c>debug</c> profile inside is laid out as Cargo 1.98 writes one, with the
    /// <c>.cargo-lock</c> and <c>.fingerprint</c> it is recognised by, so it is the one part offered.
    /// <paramref name="writeIncremental"/> adds its <c>incremental</c>, which is the incremental row's
    /// rather than the build output row's.</para>
    /// </summary>
    public static string CreateCargoProject(
        string projectDirectory,
        bool writeManifest = true,
        bool writeCacheTag = true,
        int payloadBytes = 4096,
        bool writeIncremental = false)
    {
        if (writeManifest)
        {
            WriteText(Path.Combine(projectDirectory, "Cargo.toml"), "[package]\nname = \"example\"");
            WriteText(Path.Combine(projectDirectory, "Cargo.lock"), "version = 4");
        }

        Directory(projectDirectory, "src");
        WriteText(Path.Combine(projectDirectory, "src", "main.rs"), "fn main() {}");

        var target = Directory(projectDirectory, "target");

        if (writeCacheTag)
        {
            WriteText(Path.Combine(target, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55");
        }

        CreateCargoProfile(target, "debug", payloadBytes, writeIncremental);

        return target;
    }

    /// <summary>
    /// One Cargo profile directory inside <paramref name="targetDirectory"/>, returning it: the lock
    /// file and fingerprints Cargo writes into every profile, a dependency and a built program, and
    /// where <paramref name="writeIncremental"/> asks, the incremental cache of one crate.
    /// </summary>
    public static string CreateCargoProfile(string targetDirectory, string name, int payloadBytes = 4096, bool writeIncremental = false)
    {
        var profile = Directory(targetDirectory, name);
        WriteText(Path.Combine(profile, ".cargo-lock"), string.Empty);
        Directory(profile, ".fingerprint");
        Directory(profile, "deps");
        WriteBytes(Path.Combine(profile, "deps", "libdependency.rlib"), payloadBytes);
        WriteBytes(Path.Combine(profile, "example.exe"), payloadBytes);

        if (writeIncremental)
        {
            var session = Directory(Directory(profile, "incremental"), "example-1abc");
            WriteBytes(Path.Combine(session, "query-cache.bin"), payloadBytes);
        }

        return profile;
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

    /// <summary>
    /// An Unreal project, returning its build directory of <paramref name="directoryName"/>.
    /// <paramref name="descriptor"/> null is a folder of that name in something that is not an
    /// Unreal project. The project's own folders are written with something in each, so §5.6 has
    /// content to assert: <c>Saved\Autosaves</c> above all, which is the only copy of unsaved work.
    /// </summary>
    public static string CreateUnrealProject(
        string projectDirectory,
        string directoryName = "Intermediate",
        string? descriptor = "Game.uproject",
        int payloadBytes = 4096)
    {
        if (descriptor is not null)
        {
            WriteText(Path.Combine(projectDirectory, descriptor), "{ \"FileVersion\": 3 }");
        }

        WriteText(Path.Combine(projectDirectory, "Source", "Game", "Game.cpp"), "// the user's own source");
        WriteText(Path.Combine(projectDirectory, "Config", "DefaultEngine.ini"), "[/Script/Engine.Engine]");
        WriteBytes(Path.Combine(projectDirectory, "Content", "Maps", "Main.umap"), 512);
        WriteBytes(Path.Combine(projectDirectory, "Saved", "Autosaves", "Main_Auto1.umap"), 512);
        WriteBytes(Path.Combine(projectDirectory, "Binaries", "Win64", "UnrealEditor-Game.dll"), 512);

        var build = Directory(projectDirectory, directoryName);
        WriteBytes(Path.Combine(build, "Build", "payload.bin"), payloadBytes);

        return build;
    }

    /// <summary>
    /// A Dart package after <c>pub get</c>, returning its <c>.dart_tool</c>. The package configuration
    /// carries <paramref name="generator"/> and <paramref name="configVersion"/> as pub writes them,
    /// and either set to null leaves that field out. Leaving out a piece reproduces a
    /// <c>.dart_tool</c> that is not pub's output, such as a committed test fixture or a pub
    /// workspace member.
    /// </summary>
    public static string CreateDartPackage(
        string projectDirectory,
        string? generator = "pub",
        int? configVersion = 2,
        bool writePackageConfig = true,
        bool writePackageGraph = true,
        bool writePubspec = true,
        bool writeLockFile = true,
        int payloadBytes = 4096)
    {
        if (writePubspec)
        {
            WriteText(Path.Combine(projectDirectory, "pubspec.yaml"), "name: example");
        }

        if (writeLockFile)
        {
            WriteText(Path.Combine(projectDirectory, "pubspec.lock"), "packages: {}");
        }

        WriteText(Path.Combine(projectDirectory, "lib", "main.dart"), "void main() {}");

        var tool = Directory(projectDirectory, ".dart_tool");

        if (writePackageConfig)
        {
            var fields = new List<string>();

            if (configVersion is { } version)
            {
                fields.Add($"\"configVersion\": {version}");
            }

            fields.Add("\"packages\": []");

            if (generator is not null)
            {
                fields.Add($"\"generator\": \"{generator}\"");
            }

            WriteText(Path.Combine(tool, "package_config.json"), $"{{{string.Join(", ", fields)}}}");
        }

        if (writePackageGraph)
        {
            WriteText(Path.Combine(tool, "package_graph.json"), "{\"configVersion\": 1, \"packages\": []}");
        }

        WriteBytes(Path.Combine(tool, "flutter_build", "app.dill"), payloadBytes);

        return tool;
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
