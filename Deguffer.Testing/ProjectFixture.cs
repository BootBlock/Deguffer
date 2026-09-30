using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Builds the conventional SDK-style project layout on disk: a project file with an <c>obj</c>
/// beside it holding a restore manifest and the generated MSBuild imports.
///
/// Every part is separately defeatable, because the interesting cases are the ones where a single
/// piece of evidence is missing or disagrees with the others — that is precisely where a rule which
/// recognises by name rather than by identity gets it wrong. Paths are synthetic throughout; nothing
/// here is copied from a real machine.
/// </summary>
public static class ProjectFixture
{
    /// <summary>
    /// Create a project directory and its <c>obj</c>, and return the path to the <c>obj</c>.
    /// </summary>
    /// <param name="schemaVersion">Manifest schema. Real ones observed in the wild are 3 and 4.</param>
    /// <param name="manifestProjectPath">
    /// What the manifest claims the project is. Defaults to the project file actually written;
    /// override it to reproduce a manifest copied from another tree.
    /// </param>
    /// <param name="writeProjectFile">False leaves the manifest naming a project that is not there.</param>
    /// <param name="importStem">
    /// The filename the generated imports are named after. Defaults to the project's own; override
    /// it for the mismatched-stem case.
    /// </param>
    /// <param name="rawManifest">Bypasses manifest construction entirely, for malformed JSON.</param>
    public static string CreateProject(
        string projectDirectory,
        string projectName,
        int schemaVersion = 3,
        string? manifestProjectPath = null,
        bool writeProjectFile = true,
        string? importStem = null,
        bool writeProps = true,
        bool writeTargets = true,
        string? rawManifest = null,
        int payloadBytes = 4096)
    {
        var projectFileName = projectName + ".csproj";
        var projectFile = Path.Combine(projectDirectory, projectFileName);
        var obj = Path.Combine(projectDirectory, "obj");

        Directory.CreateDirectory(LongPath.Extended(obj));

        if (writeProjectFile)
        {
            WriteText(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }

        WriteText(
            Path.Combine(obj, "project.assets.json"),
            rawManifest ?? Manifest(schemaVersion, manifestProjectPath ?? projectFile, projectName));

        var stem = importStem ?? projectFileName;

        if (writeProps)
        {
            WriteText(Path.Combine(obj, stem + ".nuget.g.props"), "<Project />");
        }

        if (writeTargets)
        {
            WriteText(Path.Combine(obj, stem + ".nuget.g.targets"), "<Project />");
        }

        // Real intermediate output always holds a per-configuration subdirectory. Its presence is
        // what makes ContentSignature the wrong primitive for this rule, so the fixtures carry it.
        var configuration = Path.Combine(obj, "Debug", "net10.0");
        Directory.CreateDirectory(LongPath.Extended(configuration));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(configuration, projectName + ".dll")), new byte[payloadBytes]);

        return obj;
    }

    /// <summary>
    /// The directory named <c>obj</c> that is not build output: third-party Wavefront models,
    /// materials and importer sidecars, with no manifest and nothing a build could regenerate.
    /// This is the shape found in a real asset pack, and the case a name check deletes silently.
    /// </summary>
    public static string CreateArtAssets(string parentDirectory)
    {
        var obj = Path.Combine(parentDirectory, "obj");
        Directory.CreateDirectory(LongPath.Extended(obj));

        foreach (var model in new[] { "barrel", "crate", "lantern" })
        {
            WriteText(Path.Combine(obj, model + ".obj"), "v 0.0 0.0 0.0");
            WriteText(Path.Combine(obj, model + ".mtl"), "newmtl default");
            WriteText(Path.Combine(obj, model + ".obj.import"), "[remap]");
        }

        return obj;
    }

    /// <summary>
    /// Create a Visual Studio solution in <paramref name="solutionDirectory"/> naming
    /// <paramref name="projectFiles"/>, each written as Visual Studio writes it, relative to the
    /// solution, and return the solution's path.
    /// </summary>
    /// <param name="xml">True for a <c>.slnx</c>, false for a <c>.sln</c>.</param>
    public static string CreateSolution(string solutionDirectory, string name, bool xml, params string[] projectFiles)
    {
        Directory.CreateDirectory(LongPath.Extended(solutionDirectory));

        var relative = projectFiles.Select(project => Path.GetRelativePath(solutionDirectory, project)).ToList();
        var solution = Path.Combine(solutionDirectory, name + (xml ? ".slnx" : ".sln"));

        WriteText(
            solution,
            xml
                ? "<Solution>\n  <Folder Name=\"/src/\">\n"
                    + string.Concat(relative.Select(r => $"    <Project Path=\"{r.Replace('\\', '/')}\" />\n"))
                    + "  </Folder>\n</Solution>\n"
                : "\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n"
                    + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{0C1A4E36-5E2B-4F5A-9A8C-1D7E3F2B6A01}\"\nEndProject\n"
                    + string.Concat(relative.Select((r, i) =>
                        $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"{Path.GetFileNameWithoutExtension(r)}\", \"{r}\", \"{{00000000-0000-0000-0000-{i:D12}}}\"\nEndProject\n"))
                    + "Global\nEndGlobal\n");

        return solution;
    }

    private static string Manifest(int version, string projectPath, string projectName) =>
        JsonSerializer.Serialize(new
        {
            version,
            project = new
            {
                restore = new
                {
                    projectPath,
                    projectName,
                    projectStyle = "PackageReference",
                },
            },
        });

    private static void WriteText(string path, string content) =>
        File.WriteAllText(LongPath.Extended(path), content);
}
