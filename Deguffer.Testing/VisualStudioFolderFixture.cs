using System.Text;

namespace Deguffer.Testing;

/// <summary>
/// The paths of one invented solution and the <c>.vs</c> folder beside it, laid out as the 42 measured
/// on a workstation were: the caches Visual Studio rebuilds beside the entries that are the user's.
/// </summary>
public sealed record VisualStudioFolder(string SolutionFolder, string SolutionFile, string Vs, string Solution, string Version)
{
    public string ProjectEvaluation => Path.Combine(Vs, "ProjectEvaluation");

    public string FileContentIndex => Path.Combine(Solution, "FileContentIndex");

    public string DesignTimeBuild => Path.Combine(Solution, "DesignTimeBuild");

    public string PrecompiledHeaders => Path.Combine(Version, "ipch");

    public string BrowseDatabase => Path.Combine(Version, "Browse.VC.db");

    public string SolutionDatabase => Path.Combine(Version, "Solution.VC.db");

    /// <summary>Every cache the row offers, in the order a test lists them.</summary>
    public IReadOnlyList<string> Caches =>
        [ProjectEvaluation, FileContentIndex, DesignTimeBuild, PrecompiledHeaders, BrowseDatabase, SolutionDatabase];

    /// <summary>The one index file, which <c>DevHub.exe</c> holds open while the solution is open.</summary>
    public string IndexFile => Path.Combine(FileContentIndex, "0f8fad5b-d9cb-469f-a165-70867728950e.vsidx");

    /// <summary>Every entry that is the user's or is not established, which §5.6 must find standing.</summary>
    public IReadOnlyList<string> Kept =>
    [
        Path.Combine(Vs, "CopilotSnapshots"),
        Path.Combine(Solution, "copilot-chat"),
        Path.Combine(Solution, "CopilotIndices"),
        Path.Combine(Solution, "lut"),
        Path.Combine(Solution, "config"),
        Path.Combine(Version, ".suo"),
        Path.Combine(Version, "DocumentLayout.json"),
        Path.Combine(Version, "fileList.bin"),
        Path.Combine(Version, "TestStore"),
        Path.Combine(Version, "Server"),
    ];
}

/// <summary>Builds a <see cref="VisualStudioFolder"/> on disk.</summary>
public static class VisualStudioFolderFixture
{
    /// <param name="solutionFolder">The folder the solution file is written to.</param>
    /// <param name="solutionFileName">
    /// <c>App.sln</c> writes to <c>.vs\App</c>, and <c>App.slnx</c> to <c>.vs\App.slnx</c>, as Visual
    /// Studio does.
    /// </param>
    /// <param name="version">The version folder, <c>v17</c> for Visual Studio 2022.</param>
    public static VisualStudioFolder Create(string solutionFolder, string solutionFileName = "App.sln", string version = "v17")
    {
        var solutionFile = Path.Combine(solutionFolder, solutionFileName);
        var vs = Path.Combine(solutionFolder, ".vs");
        var inner = Path.GetExtension(solutionFileName).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? solutionFileName
            : Path.GetFileNameWithoutExtension(solutionFileName);
        var folder = new VisualStudioFolder(solutionFolder, solutionFile, vs, Path.Combine(vs, inner), Path.Combine(vs, inner, version));

        Write(solutionFile, "Microsoft Visual Studio Solution File, Format Version 12.00\r\n");

        foreach (var part in new[] { "projects", "metadata", "strings" })
        {
            WriteBytes(Path.Combine(folder.ProjectEvaluation, $"app.{part}.v10.bin"));
        }

        WriteBytes(folder.IndexFile);
        WriteBytes(Path.Combine(folder.FileContentIndex, "read.lock"));
        Directory.CreateDirectory(Path.Combine(folder.FileContentIndex, "merges"));
        WriteBytes(Path.Combine(folder.DesignTimeBuild, ".dtbcache.v2"));
        WriteBytes(Path.Combine(folder.PrecompiledHeaders, "AutoPCH", "5820ba93e90ae8c8", "MAIN.ipch"));
        WriteDatabase(folder.BrowseDatabase);
        WriteDatabase(folder.SolutionDatabase);

        Write(Path.Combine(vs, "CopilotSnapshots", "1", "state.mpack"), "a snapshot of a file");
        Write(Path.Combine(folder.Solution, "copilot-chat", "92494b03", "sessions", "1"), "a conversation");
        Write(Path.Combine(folder.Solution, "CopilotIndices", "18.0", "CodeChunks.db"), "an index");
        Write(Path.Combine(folder.Solution, "lut", "coverage"), "coverage");
        Write(Path.Combine(folder.Solution, "config", "applicationhost.config"), "<configuration />");
        WriteBytes(Path.Combine(folder.Version, ".suo"));
        Write(Path.Combine(folder.Version, "DocumentLayout.json"), "{}");
        WriteBytes(Path.Combine(folder.Version, "fileList.bin"));
        Write(Path.Combine(folder.Version, "TestStore", "results"), "results");
        WriteDatabase(Path.Combine(folder.Version, "Server", "sqlite3", "storage.ide"));

        return folder;
    }

    /// <summary>An SQLite database's sixteen-byte header, then a page of nothing.</summary>
    public static void WriteDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("SQLite format 3\0"), .. new byte[4080]]);
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void WriteBytes(string path, int length = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}
