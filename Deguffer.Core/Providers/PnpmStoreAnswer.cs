using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What pnpm said when asked where its store is: the store, and the npm global configuration file it
/// reads beside its own (pnpm 10 and earlier). That file sits under a prefix a configuration file can
/// itself set, so no fixed list of paths finds it, and pnpm is asked for it alongside the store.
/// </summary>
/// <param name="Store">
/// The store <c>pnpm store path</c> reported, or null where it failed or named no folder that has a
/// containing directory.
/// </param>
/// <param name="GlobalConfig">
/// The file <c>pnpm config get globalconfig</c> reported, or null where it failed or printed no full
/// path.
/// </param>
internal sealed record PnpmStoreAnswer(string? Store, string? GlobalConfig)
{
    /// <summary>
    /// Whether pnpm answered both questions. Only then may the answer outlive the pass: without the
    /// global configuration's path, an edit to that file would go unnoticed and the remembered store
    /// would be one pnpm has stopped using. The store alone still serves the pass that asked.
    /// </summary>
    public bool IsAnswer => Store is not null && GlobalConfig is not null;

    /// <summary>
    /// Everything pnpm reads to say where its store is (https://pnpm.io/npmrc,
    /// https://pnpm.io/settings, https://pnpm.io/cli/store), for <see cref="ToolAnswerMemory"/>.
    ///
    /// <para>A variable naming a folder is read from the block the tool is started with. A value that
    /// is not a full path is declared as it stands: pnpm would resolve it against a folder Deguffer
    /// cannot see it resolve, and the unqualified path makes the reading incomplete, so the answer is
    /// asked again every pass rather than remembered on a guess.</para>
    /// </summary>
    /// <param name="environment">Where the profile's folders and the tool's variables are read.</param>
    /// <param name="pnpm">The executable that answered.</param>
    public ToolInputs InputsFor(IUserEnvironment environment, string pnpm)
    {
        var beside = Path.GetDirectoryName(pnpm) ?? pnpm;
        var files = new List<string>
        {
            // An npm global install, a standalone pnpm.exe or a Corepack shim: the program, the Node
            // that runs it, and the version of pnpm the shim starts.
            pnpm,
            Path.Combine(beside, "node.exe"),
            Path.Combine(beside, "node_modules", "pnpm", "package.json"),
            Path.Combine(environment.UserProfile, ".npmrc"),
        };

        if (environment.FindExecutable("node") is { } node)
        {
            files.Add(node);
        }

        AddConfigurationFolder(files, Path.Combine(environment.LocalAppData, "pnpm", "config"));

        if (Declared(environment.GetToolEnvironmentVariable("XDG_CONFIG_HOME")) is { } xdg)
        {
            AddConfigurationFolder(files, Path.Combine(xdg, "pnpm"));
        }

        if (Declared(environment.GetToolEnvironmentVariable("npm_config_userconfig")) is { } userConfig)
        {
            files.Add(userConfig);
        }

        if (GlobalConfig is not null)
        {
            files.Add(GlobalConfig);
        }

        files.AddRange(ToolInputs.InEachFolderUp(
            ToolInputs.WorkingDirectory(environment), ".npmrc", "pnpm-workspace.yaml", "package.json"));

        return new ToolInputs
        {
            Files = files,
            Folders =
            [
                // Corepack's home holds the pnpm versions it downloaded and the one it starts by
                // default, a level below the folder for each tool.
                new ListedFolder(
                    Declared(environment.GetToolEnvironmentVariable("COREPACK_HOME"))
                        ?? Path.Combine(environment.LocalAppData, "node", "corepack"),
                    Depth: 2),

                // pnpm's home holds the versions `pnpm self-update` and `pnpm env` switched to.
                new ListedFolder(
                    Declared(environment.GetToolEnvironmentVariable("PNPM_HOME"))
                        ?? Path.Combine(environment.LocalAppData, "pnpm")),
            ],
        };
    }

    private static void AddConfigurationFolder(List<string> files, string folder)
    {
        files.Add(Path.Combine(folder, "rc"));
        files.Add(Path.Combine(folder, "config.yaml"));
        files.Add(Path.Combine(folder, "auth.ini"));
    }

    private static string? Declared(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : LongPath.Configured(value) ?? value;
}
