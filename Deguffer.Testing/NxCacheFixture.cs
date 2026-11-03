namespace Deguffer.Testing;

/// <summary>
/// One Nx cache and the index beside it, as Nx 24.0.3's source lays them out: an entry folder per
/// task hash holding the task's outputs, <c>terminalOutputs</c> holding each task's log by hash,
/// <c>batchOutputs</c> holding a batch log, and the index's SQLite database with its write-ahead log.
/// </summary>
/// <param name="Folder">The folder holding both: <c>~\.nx\&lt;id&gt;</c> or a workspace's <c>.nx</c>.</param>
/// <param name="Cache">The cache folder, the one path a plan removes.</param>
/// <param name="Index">The folder holding the index, which must survive.</param>
/// <param name="Databases">The index's files, the database and its write-ahead log.</param>
public sealed record NxCache(string Folder, string Cache, string Index, IReadOnlyList<string> Databases)
{
    /// <summary>A task's output inside its entry, which goes with the cache.</summary>
    public string Entry => Path.Combine(Cache, NxCacheFixture.Hash, "dist", "main.js");
}

public static class NxCacheFixture
{
    /// <summary>A task hash as Nx writes one, the decimal digits of a 64-bit hash.</summary>
    public const string Hash = "12279349187470632893";

    /// <summary>The database Nx 24.0.3 names for a machine: its id, then the schema version.</summary>
    public const string CurrentDatabase = "4d1c7e0b-machine-v6.db";

    /// <summary>
    /// Lay out a cache in <paramref name="folder"/>, with its index in <paramref name="indexName"/>
    /// holding <paramref name="databases"/>, or no index folder at all where none are given.
    /// </summary>
    public static NxCache Create(string folder, string indexName, params string[] databases)
    {
        var cache = Path.Combine(folder, "cache");
        var index = Path.Combine(folder, indexName);

        Write(Path.Combine(cache, Hash, "dist", "main.js"), new string('x', 4096));
        Write(Path.Combine(cache, "98765432109876543", "code"), "0");
        Write(Path.Combine(cache, "terminalOutputs", Hash), "> nx run app:build");
        Write(Path.Combine(cache, "batchOutputs", "batch-1.log"), "batch");

        List<string> files = [];

        foreach (var database in databases)
        {
            var path = Path.Combine(index, database);
            Write(path, "SQLite format 3\0");
            files.Add(path);
        }

        return new NxCache(folder, cache, index, files);
    }

    /// <summary>The usual case: one current database and its write-ahead log.</summary>
    public static NxCache CreateCurrent(string folder, string indexName) =>
        Create(folder, indexName, CurrentDatabase, CurrentDatabase + "-wal");

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
