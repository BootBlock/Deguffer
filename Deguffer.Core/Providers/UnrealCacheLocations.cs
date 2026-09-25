using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One place a Zen store's data may be: the directory it sits in, and its path below that.
/// </summary>
/// <param name="Root">The directory holding the store, which survives it.</param>
/// <param name="RelativePath">The store, relative to <paramref name="Root"/>.</param>
/// <param name="IsConfigured">
/// Whether a setting named this location rather than Unreal's own default. A configured location
/// is a folder somebody chose, so it is a store only where Zen's own <c>root_manifest</c> is in it.
/// </param>
internal readonly record struct ZenStoreLocation(string Root, string RelativePath, bool IsConfigured)
{
    public string Path => System.IO.Path.Combine(Root, RelativePath);
}

/// <summary>
/// Where the Unreal Engine derived-data caches that every project shares are on this machine. The
/// facts here were read from the engine's own source for 5.3, 5.4, 5.5 and 5.8, and from Zen's.
///
/// <para><b>Two kinds of cache, and a machine can hold both.</b> Unreal Engine 5.3 and earlier keep
/// a filesystem cache at <c>%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache</c> for an installed
/// engine. Later versions default to a Zen store and put the old cache into a delete-only mode that
/// expires anything unused for eight days, so on a machine that has moved on it only shrinks.</para>
///
/// <para><b>Zen's default moved at 5.4.</b> Unreal Engine 5.1 to 5.3 keep it at
/// <c>%PROGRAMDATA%\Epic\Zen\Data</c>, and 5.4 and later at
/// <c>%LOCALAPPDATA%\UnrealEngine\Common\Zen\Data</c>. Epic's documentation still gives the first
/// for 5.4, which is out of date. The engine removes the old store itself only in some versions and
/// only when no server holds it, so a machine that ran both can hold both. The server's own
/// installation is a <c>Zen\Install</c> folder beside each, and is never a target.</para>
///
/// <para><b>A setting can move the store, and each one is read.</b> A local cache path, set by the
/// <c>UE-LocalDataCachePath</c> environment variable or by the editor's own preference in the
/// registry, moves Zen to a <c>Zen</c> folder inside it. Zen's own data path moves the store to
/// exactly the folder named, and can be set by two environment variables or by a registry value.
/// Which one wins depends on the engine version and on which are set, so every one of them is
/// probed. A path given on one editor's command line is recorded nowhere and cannot be found.</para>
///
/// <para><b>A folder a setting names is a store only where Zen made it one.</b> It is a folder
/// somebody chose, and Zen writes into whatever folder it is given, so the marker alone proves only
/// that Zen has been there. The folder is reached only where Zen's <c>root_manifest</c> is in it and
/// every entry at its top is one Zen writes (<see cref="ZenEntries"/>). One file of anybody else's
/// and it is left alone, which is §5.2's direction. The filesystem cache an older engine wrote straight
/// into a local cache path is not reached either: nothing classifies its entries apart from anything
/// else in that folder, §5.2 leaves the unrecognised alone, and the delete-only mode empties it
/// anyway.</para>
/// </summary>
internal static class UnrealCacheLocations
{
    /// <summary>The file Zen writes at the top of every store it creates.</summary>
    public const string StoreMarker = "root_manifest";

    /// <summary>
    /// Every entry Zen's storage server writes at the top of its data folder, read from Zen's own
    /// source. An entry not named here is not known to be Zen's, so a folder holding one is not
    /// treated as a store. A newer Zen that adds an entry makes its store unreached until the name
    /// is added, which is the safe direction to be out of date in.
    /// </summary>
    public static readonly IReadOnlySet<string> ZenEntries = new HashSet<string>(
    [
        ".lock", StoreMarker, StoreMarker + ".ignore_schema_mismatch", "state_marker", "zen_cfg.lua",
        ".sentry-native", "cas", "cache", "projects", "builds", "builds_cas", "obj", "gc", "auth",
        "sessions", "traces", "logs", "functions", "hub", "servers", "recordings", "orch", "horde",
        "cloud",
    ],
    StringComparer.OrdinalIgnoreCase);

    /// <summary>The variable, and the registry value, that set the local cache path.</summary>
    private const string LocalCachePath = "UE-LocalDataCachePath";

    /// <summary>
    /// The key, under <c>HKEY_CURRENT_USER</c>, where the editor keeps its global local cache path.
    /// </summary>
    private const string LocalCachePathKey = @"Software\Epic Games\GlobalDataCachePath";

    /// <summary>The key, under <c>HKEY_CURRENT_USER</c>, where Zen's own data path can be set.</summary>
    private const string ZenKey = @"Software\Epic Games\Zen";

    /// <summary>The environment variables that set Zen's data path outright.</summary>
    private static readonly string[] ZenDataPathVariables = ["UE-ZenSubprocessDataPath", "UE-ZenDataPath"];

    /// <summary><c>%LOCALAPPDATA%\UnrealEngine</c>, where every engine version keeps its settings.</summary>
    public static string EngineRoot(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "UnrealEngine");

    /// <summary><c>%LOCALAPPDATA%\UnrealEngine\Common</c>, the folder the engine versions share.</summary>
    public static string Common(IUserEnvironment environment) =>
        Path.Combine(EngineRoot(environment), "Common");

    /// <summary>The two places Unreal puts a Zen store by default, the current one first.</summary>
    public static IReadOnlyList<ZenStoreLocation> DefaultStores(
        IUserEnvironment environment,
        ISystemDirectories system) =>
    [
        new(EngineRoot(environment), Path.Combine("Common", "Zen", "Data"), IsConfigured: false),
        new(Path.Combine(system.ProgramData, "Epic"), Path.Combine("Zen", "Data"), IsConfigured: false),
    ];

    /// <summary>
    /// Every place a setting puts a Zen store, fully qualified and without duplicates. A value that
    /// is not a full path, such as the <c>None</c> Unreal reads as "no local cache", is no location.
    /// Neither is a Zen data path naming a volume's root, which would make the whole volume the
    /// store. A local cache path may be a volume's root, because the store is the <c>Zen</c> folder
    /// inside it.
    /// </summary>
    public static IReadOnlyList<ZenStoreLocation> ConfiguredStores(IUserEnvironment environment)
    {
        var localCachePaths = new[]
        {
            environment.GetEnvironmentVariable(LocalCachePath),
            environment.ReadCurrentUserRegistryValue(LocalCachePathKey, LocalCachePath),
        };

        var dataPaths = ZenDataPathVariables
            .Select(environment.GetEnvironmentVariable)
            .Append(environment.ReadCurrentUserRegistryValue(ZenKey, "DataPath"));

        return
        [
            .. localCachePaths
                .Select(LongPath.Configured)
                .OfType<string>()
                .Select(chosen => new ZenStoreLocation(chosen, "Zen", IsConfigured: true))
                .Concat(dataPaths
                    .Select(LongPath.Configured)
                    .OfType<string>()
                    .Where(store => Path.GetDirectoryName(store) is not null)
                    .Select(store => new ZenStoreLocation(
                        Path.GetDirectoryName(store)!, Path.GetFileName(store), IsConfigured: true)))
                .DistinctBy(store => store.Path, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
