namespace Deguffer.Testing;

/// <summary>
/// Roslyn's solution indexes, laid out as Roslyn writes them. Every name here is invented, in the shape
/// Roslyn builds: a file name cut to twenty characters, a dash, and a Base64 checksum.
/// </summary>
public static class RoslynCacheFixture
{
    public const string Host = "devenv.exe-TestHostChecksumAAAAA==";

    public const string OtherHost = "ServiceHub.RoslynCod-OtherHostChecksumBBB==";

    public const string Solution = "Sample.sln-SolutionChecksumCCCCC==";

    public const string OtherSolution = "Another.sln-SolutionChecksumDDDDD==";

    public static string CacheIn(FakeUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "Microsoft", "VisualStudio", "Roslyn", "Cache");

    /// <summary>A program's set holding a whole index for each solution named. Returns the set's path.</summary>
    public static string CreateHost(string cache, string host, params string[] solutions)
    {
        var directory = Path.Combine(cache, host);
        Directory.CreateDirectory(directory);

        foreach (var solution in solutions)
        {
            CreateIndex(directory, solution);
        }

        return directory;
    }

    /// <summary>
    /// One solution's index, with every file Roslyn keeps beside the database. Returns the directory the
    /// files are in.
    /// </summary>
    public static string CreateIndex(string host, string solution)
    {
        var files = IndexIn(host, solution);
        Directory.CreateDirectory(files);

        File.WriteAllBytes(Path.Combine(files, "storage.ide"), new byte[65536]);
        File.WriteAllBytes(Path.Combine(files, "storage.ide-wal"), new byte[16384]);
        File.WriteAllBytes(Path.Combine(files, "storage.ide-shm"), new byte[32768]);
        File.WriteAllBytes(Path.Combine(files, "db.lock"), []);

        return files;
    }

    public static string IndexIn(string host, string solution) => Path.Combine(host, solution, "sqlite3", "v2");
}
