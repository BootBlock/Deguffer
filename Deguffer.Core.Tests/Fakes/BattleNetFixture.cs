namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The Battle.net launcher's folder as it was measured: the launcher's own cache and logs, its
/// account data and database, and the Chromium Embedded Framework's user-data folder with one
/// partition in it. Each part is created only when a test asks, so a test states what it needs.
/// </summary>
public sealed class BattleNetFixture(string localAppData)
{
    public string Launcher { get; } = Path.Combine(localAppData, "Battle.net");

    public string Cache => Path.Combine(Launcher, "Cache");

    public string Logs => Path.Combine(Launcher, "Logs");

    public string Account => Path.Combine(Launcher, "Account");

    public string Database => Path.Combine(Launcher, "CachedData.db");

    public string BrowserCaches => Path.Combine(Launcher, "BrowserCaches");

    /// <summary>The partition the launcher names <c>common</c>.</summary>
    public string Common => Path.Combine(BrowserCaches, "common");

    /// <summary>The framework's settings file, which holds the key that decrypts the sign-in.</summary>
    public const string Marker = "LocalPrefs.json";

    /// <summary>The user-data folder, with its marker, and nothing else.</summary>
    public string CreateBrowserCaches() => Mark(BrowserCaches);

    /// <summary>A partition, with its own marker, under a marked user-data folder.</summary>
    public string CreatePartition(string name = "common")
    {
        CreateBrowserCaches();
        return Mark(Path.Combine(BrowserCaches, name));
    }

    /// <summary>
    /// Everything the measured folder held, each directory with one file in it so it measures above
    /// zero. The launcher's cache is laid out as hash buckets, as it is on disk.
    /// </summary>
    public void CreateMeasuredLayout()
    {
        var common = CreatePartition();

        foreach (var name in new[]
                 {
                     "Code Cache", "GPUCache", "DawnCache", @"Cache\Cache_Data", "Local Storage",
                     "Session Storage", "Network", "Storage", "blob_storage", "shared_proto_db",
                     "VideoDecodeStats",
                 })
        {
            Populate(Path.Combine(common, name));
        }

        Populate(Path.Combine(Cache, "0a"), "0a000000000000000000000000000001");
        Populate(Path.Combine(Cache, "ff"), "ff000000000000000000000000000002");
        Populate(Logs, "battle.net-20000101T000000.000000.log");
        Populate(Path.Combine(Account, "12345678"), "account.db");
        File.WriteAllBytes(Database, new byte[64]);
    }

    /// <summary>A directory with one file of <paramref name="bytes"/> in it.</summary>
    public static string Populate(string directory, string name = "entry.bin", int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);
        return directory;
    }

    private static string Mark(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Marker), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        return directory;
    }
}
