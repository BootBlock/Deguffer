using System.Reflection;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every row's <see cref="ICleanupProvider.ToolRoots"/> lie inside its
/// <see cref="ICleanupProvider.ToolRootScope"/>. A root outside it is one a question about its folder
/// never reads, so the row asking would assert as a survivor a child this row removes, and §5.6 would
/// report a correct removal as a failure.
///
/// <para>The subject is <see cref="CleanupPlanner.CreateDefault"/>'s own list, so a row added later
/// is held to this the moment it is registered.</para>
/// </summary>
public sealed class ToolRootScopeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A row that declares roots states their scope beside them, in the same type or one derived from
    /// it, so no row inherits a scope written for roots it replaced. This holds where the fakes cannot
    /// make a row declare anything, such as a root found only by a walk.
    /// </summary>
    [Fact]
    public void EveryRowThatDeclaresRootsStatesTheirScope()
    {
        using var shipped = new ShippedProviders();
        List<string> unstated = [];

        foreach (var row in shipped.Providers)
        {
            var type = row.GetType();
            var roots = Declarer(type, nameof(ICleanupProvider.ToolRoots));
            var scope = Declarer(type, nameof(ICleanupProvider.ToolRootScope));

            if (roots != typeof(CleanupProviderBase) && (scope == typeof(CleanupProviderBase) || !roots.IsAssignableFrom(scope)))
            {
                unstated.Add($"{type.Name}: roots declared in {roots.Name}, scope in {scope.Name}");
            }
        }

        Assert.Empty(unstated);
    }

    /// <summary>Every root each row declares on a bare profile lies inside its scope.</summary>
    [Fact]
    public async Task EveryRootOnABareProfileLiesInsideItsRowsScope() =>
        Assert.Empty(await OutsideTheirScope(new FakeUserEnvironment(_temp.Path)));

    /// <summary>
    /// Every root each row declares lies inside its scope on a profile where the rows find what they
    /// look for in their usual places.
    /// </summary>
    [Fact]
    public async Task EveryRootOnAPopulatedProfileLiesInsideItsRowsScope() =>
        Assert.Empty(await OutsideTheirScope(Populated()));

    /// <summary>
    /// Every root each row declares lies inside its scope where every variable and registry value that
    /// moves a tool's folder moves it somewhere else, and a started tool would be told a third place.
    /// </summary>
    [Fact]
    public async Task EveryRootOnAMovedProfileLiesInsideItsRowsScope() =>
        Assert.Empty(await OutsideTheirScope(Moved()));

    /// <summary>
    /// The profiles above make every row that declares roots declare some, so the sweeps are not
    /// passing over rows that declared nothing.
    /// </summary>
    [Fact]
    public async Task ThePopulatedProfilesMakeEveryDeclaringRowDeclare()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);

        foreach (var environment in new[] { Populated(), Moved() })
        {
            foreach (var row in CleanupPlanner.CreateDefault(environment: environment).Providers)
            {
                if ((await DeclaredRoots.ReadAsync(row, default)).Count > 0)
                {
                    declared.Add(row.GetType().Name);
                }
            }
        }

        using var shipped = new ShippedProviders();
        var silent = shipped.Providers
            .Where(row => Declarer(row.GetType(), nameof(ICleanupProvider.ToolRoots)) != typeof(CleanupProviderBase))
            .Select(row => row.GetType().Name)
            .Where(name => !declared.Contains(name) && !DeclaresOnlyFromASettingsFile.Contains(name))
            .Order(StringComparer.Ordinal);

        Assert.Empty(silent);
    }

    /// <summary>
    /// Rows the profiles above do not make declare, each scoped anywhere because only a settings file
    /// or a library list says where its roots are, so no sweep can find a root outside its scope.
    /// </summary>
    private static readonly HashSet<string> DeclaresOnlyFromASettingsFile = new(StringComparer.Ordinal)
    {
        nameof(EmbyTranscodeProvider),
        nameof(FileHistoryProvider),
        nameof(SteamShaderCacheProvider),
    };

    /// <summary>The variables that move a folder some row declares a root in.</summary>
    private static readonly string[] MovingVariables =
    [
        "BUN_INSTALL", "CARGO_HOME", "CCACHE_DIR", "CLAUDE_CONFIG_DIR", "COURSIER_ARCHIVE_CACHE",
        "COURSIER_CACHE", "COURSIER_DIGEST_BASED_CACHE", "COURSIER_JVM_CACHE",
        "COURSIER_PRIVILEDGED_ARCHIVE_CACHE", "DENO_DIR", "electron_config_cache", "GRADLE_USER_HOME",
        "HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "JELLYFIN_CACHE_DIR", "PIPENV_CACHE_DIR",
        "PLAYWRIGHT_BROWSERS_PATH", "PNPM_HOME", "PRE_COMMIT_HOME", "PULUMI_HOME", "PUPPETEER_CACHE_DIR",
        "RESTIC_CACHE_DIR", "SCCACHE_DIR", "SQUIRREL_TEMP", "VAGRANT_HOME", "VSCODE_EXTENSIONS",
        "XDG_CACHE_HOME", "ZIG_GLOBAL_CACHE_DIR",
    ];

    /// <summary>A profile holding what the rows that find their roots look for, in the usual places.</summary>
    private FakeUserEnvironment Populated()
    {
        var environment = new FakeUserEnvironment(Path.Combine(_temp.Path, "populated"));
        var profile = environment.UserProfile;
        var local = environment.LocalAppData;
        var roaming = environment.RoamingAppData;

        PopulateClaudeCode(Path.Combine(profile, ".claude"));
        Directory.CreateDirectory(Path.Combine(local, "claude-cli-nodejs", "Cache"));

        var browser = Path.Combine(local, "Google", "Chrome", "User Data");
        Directory.CreateDirectory(Path.Combine(browser, "Default"));
        File.WriteAllText(Path.Combine(browser, "Local State"), "{}");

        var code = Path.Combine(roaming, "Code");
        Directory.CreateDirectory(
            Path.Combine(code, "User", "globalStorage", "saoudrizwan.claude-dev", "puppeteer", ".chromium-browser-snapshots"));
        File.WriteAllText(Path.Combine(code, "Local State"), "{}");
        File.WriteAllText(Path.Combine(code, "User", "globalStorage", "state.vscdb"), "");
        PopulateCodeExtensions(Path.Combine(profile, ".vscode", "extensions"));

        var steam = Directory.CreateDirectory(Path.Combine(_temp.Path, "populated-steam")).FullName;
        File.WriteAllText(Path.Combine(steam, "steam.exe"), "");
        environment.WithRegistryValue(SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, steam);

        var squirrel = Path.Combine(local, "Acme");
        Directory.CreateDirectory(Path.Combine(squirrel, "app-1.0.0"));
        File.WriteAllText(Path.Combine(squirrel, "Update.exe"), "");

        Directory.CreateDirectory(Path.Combine(profile, ".affinity", "Common", "2.0"));
        Directory.CreateDirectory(Path.Combine(local, "CapCut", "Apps"));
        Directory.CreateDirectory(Path.Combine(local, "JetBrains", "Rider2023.1"));
        Directory.CreateDirectory(Path.Combine(local, "jellyfin"));

        return environment;
    }

    /// <summary>
    /// A profile where every variable that moves a tool's folder names a folder of its own, a started
    /// tool is told yet another, and Plex's registry values move its folders too.
    /// </summary>
    private FakeUserEnvironment Moved()
    {
        var environment = new FakeUserEnvironment(Path.Combine(_temp.Path, "moved"));
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");

        foreach (var variable in MovingVariables)
        {
            var current = Directory.CreateDirectory(Path.Combine(elsewhere, "current", variable)).FullName;
            var tool = Directory.CreateDirectory(Path.Combine(elsewhere, "tool", variable)).FullName;
            environment.WithEnvironmentVariable(variable, current).WithToolEnvironmentVariable(variable, tool);
        }

        PopulateClaudeCode(Path.Combine(elsewhere, "current", "CLAUDE_CONFIG_DIR"));
        PopulateCodeExtensions(Path.Combine(elsewhere, "current", "VSCODE_EXTENSIONS"));

        foreach (var (value, folder) in new[]
        {
            (PlexServerLayout.DataFolderValue, "plex-data"),
            (PlexServerLayout.TranscoderValue, "plex-transcoder"),
            (PlexServerLayout.DownloadsValue, "plex-downloads"),
        })
        {
            var moved = Directory.CreateDirectory(Path.Combine(elsewhere, folder)).FullName;
            environment.WithRegistryValue(PlexServerLayout.RegistryKey, value, moved);
        }

        Directory.CreateDirectory(Path.Combine(elsewhere, "plex-data", PlexServerLayout.FolderName));

        return environment;
    }

    private static void PopulateClaudeCode(string home)
    {
        new ClaudeCodeFixture(home).Conversation(ClaudeCodeFixture.SessionA, age: ClaudeCodeFixture.Old);
        Directory.CreateDirectory(Path.Combine(home, "file-history"));
    }

    private static void PopulateCodeExtensions(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "extensions.json"),
            """[{"identifier":{"id":"a.b"},"location":{"path":"/x/a.b-1.0.0"}}]""");
    }
    private static async Task<IReadOnlyList<string>> OutsideTheirScope(FakeUserEnvironment environment)
    {
        var planner = CleanupPlanner.CreateDefault(environment: environment);
        List<string> outside = [];

        foreach (var row in planner.Providers)
        {
            var scope = row.ToolRootScope;

            foreach (var root in await DeclaredRoots.ReadAsync(row, default))
            {
                if (LongPath.Configured(root.Path) is { } folder && !scope.Covers(folder))
                {
                    outside.Add($"{row.GetType().Name}: {root.Path} is outside {scope}");
                }
            }
        }

        return outside;
    }

    private static Type Declarer(Type row, string property) =>
        row.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.DeclaringType!;
}
