using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where the Unity Editor keeps the GI cache every project on the machine shares, and what sits beside
/// it.
///
/// <para><b>The default is inferred, and only the default is reached.</b> Unity's GI cache preferences
/// say the cache is "stored in the Caches folder", which on Windows is <c>%LOCALAPPDATA%\Unity\Caches</c>,
/// the folder that also holds Unity's build cache. The cache's own folder there is <c>GiCache</c>, the
/// name Unity's documentation gives the folder the Editor creates when it starts. So Deguffer looks at
/// that one path and nowhere else. If Unity kept the cache somewhere else by default, the cost is a
/// cache that is never found, not a wrong deletion.</para>
///
/// <para><b>A moved cache is named, never reached.</b> <b>Preferences → GI Cache → Custom cache
/// location</b> moves it, and the Editor stores that as the <c>GICacheEnableCustomPath</c> and
/// <c>GICacheFolder</c> preferences (Unity's C# reference source,
/// <c>PreferencesSettingsProviders.cs</c>). The folder is one the user picked. Unity's documentation
/// does not say whether the Editor writes into it or into a folder of its own inside it, and the cache
/// files are named by a hash with no marker to recognise them by, so nothing tells Unity's files there
/// from the user's. The plan names the folder and sends the user to Unity's own Clean Cache, the one
/// route Unity provides (§5.1).</para>
///
/// <para><b>§5.2.</b> Neither <c>%LOCALAPPDATA%\Unity</c> nor <c>Caches</c> is ever a target, and nothing
/// in either is enumerated. <c>bee</c> beside the cache is Unity's machine-wide build cache, and Unity
/// warns that "Modifying or deleting these files outside of Unity can lead to unexpected issues during
/// subsequent builds", so it is Tier 4. <c>CurlRequestCache.db</c> is not classified, so it is Tier 4
/// too.</para>
/// </summary>
public static class UnityGiCacheLayout
{
    /// <summary>The cache's own folder, inside <see cref="CachesFolder"/>.</summary>
    public const string CacheFolder = "GiCache";

    /// <summary>The folder inside Unity's own that holds the GI cache and the build cache.</summary>
    public const string CachesFolder = "Caches";

    /// <summary>The preference that turns the custom location on.</summary>
    public const string CustomLocationPreference = "GICacheEnableCustomPath";

    /// <summary>The preference that names the custom location.</summary>
    public const string CustomFolderPreference = "GICacheFolder";

    private const string UnityFolder = "Unity";

    /// <summary>
    /// §5.3. The Editor's process, which is the same program in batch mode, so a command-line build or
    /// a CI agent holds the cache back as an open window does.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["Unity"];

    /// <summary>What the user is told about the cache, in the plan and in Explore.</summary>
    public const string CacheReason =
        "Intermediate files Unity's Editor produced while it precomputed lighting. Unity produces them "
        + "again the next time a scene's lighting is baked.";

    /// <summary>
    /// What §5.6 asserts survived, by name, since nothing here is enumerated. The relative paths are
    /// under <c>%LOCALAPPDATA%\Unity</c>.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] Survivors =
    [
        (Path.Combine(CachesFolder, "bee"),
            "Unity's build cache, which every project shares. Unity warns that changing it from outside "
            + "Unity can break later builds, so it is left alone."),
        (Path.Combine(CachesFolder, "CurlRequestCache.db"),
            "A database Unity keeps. Nobody has established what is in it, so it is left alone."),
        ("cache", "Unity's Package Manager cache, which every project shares."),
        ("Editor", "The Unity Editor's logs."),
        ("licenses", "Your Unity licence."),
    ];

    /// <summary>Unity's own folder under <c>%LOCALAPPDATA%</c>.</summary>
    public static string UnityFolderIn(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, UnityFolder);

    /// <summary>The <c>Caches</c> folder inside it.</summary>
    public static string CachesFolderIn(IUserEnvironment environment) =>
        Path.Combine(UnityFolderIn(environment), CachesFolder);

    /// <summary>Where the GI cache is when nothing has moved it.</summary>
    public static string DefaultCacheIn(IUserEnvironment environment) =>
        Path.Combine(CachesFolderIn(environment), CacheFolder);

    /// <summary>
    /// Unity's folder as a <see cref="DeclaredRoot"/> with the default cache as its one location. The
    /// folder and <c>Caches</c> are never targets, and the build cache and Unity's other data beside the
    /// cache are asserted to survive.
    /// </summary>
    public static DeclaredRoot Declare(IUserEnvironment environment) => new(
        UnityFolderIn(environment),
        "Unity's own folder must survive. Its licence, logs and the caches every project shares are in "
        + "it, and only the GI cache inside it is removed.",
        RequiresElevation: false,
        [new DeclaredLocation(Path.Combine(CachesFolder, CacheFolder), CacheReason, ReportsAge: false)],
        Survivors);

    /// <summary>
    /// §5.2 as §7.1 reads it while no Editor runs. Unity's folder recognises nothing, and <c>Caches</c>
    /// recognises only the GI cache.
    /// </summary>
    public static IReadOnlyList<ToolRoot> Roots(IUserEnvironment environment) =>
    [
        new ToolRoot(
            UnityFolderIn(environment),
            "This is Unity's own folder, with your licence, the Editor's logs and the caches every project "
            + "shares. Deguffer removes only the GI cache inside it.",
            static _ => false),
        ToolRoot.Of(
            CachesFolderIn(environment),
            "This is the folder Unity keeps its shared caches in. Deguffer removes only the GI cache, never "
            + "the build cache beside it.",
            new DisposableChildSet(
            [
                new ChildClassification(CacheFolder, SafetyTier.RegenerableWithCost, CacheReason),
            ])),
    ];

    /// <summary>
    /// Where Unity's settings move the cache, or null where they leave it at
    /// <see cref="DefaultCacheIn"/>.
    /// </summary>
    public static UnityGiCacheMove? MovedTo(IUserEnvironment environment)
    {
        var preferences = UnityEditorPreferences.Read(environment);

        if (!preferences.IsOn(CustomLocationPreference))
        {
            return null;
        }

        // A folder that is not fully qualified names no place Deguffer can say Unity resolves it against.
        if (LongPath.Configured(preferences.Text(CustomFolderPreference)) is not { } folder)
        {
            return new UnityGiCacheMove(Folder: null);
        }

        // The default spelled another way, with Unity's forward slashes for one, moves nothing.
        return Path.TrimEndingDirectorySeparator(folder).Equals(DefaultCacheIn(environment), StringComparison.OrdinalIgnoreCase)
            ? null
            : new UnityGiCacheMove(folder);
    }
}

/// <summary>Unity's settings moving the GI cache away from its default folder.</summary>
/// <param name="Folder">
/// The folder they name, or null where the custom location is turned on and no folder could be read,
/// so Unity is using a place Deguffer cannot name.
/// </param>
public sealed record UnityGiCacheMove(string? Folder);
