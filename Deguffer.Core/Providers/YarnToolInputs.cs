using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything Yarn reads to say which Yarn it is and where it keeps its caches, for
/// <see cref="ToolAnswerMemory"/>. Every variable Yarn is started with is in the fingerprint anyway,
/// so <c>YARN_*</c>, <c>COREPACK_*</c> and any <c>${VAR}</c> an rc file names need no entry.
/// </summary>
internal static class YarnToolInputs
{
    /// <summary>
    /// What decides which Yarn answers <c>yarn -v</c>: the program and the Node.js it runs on, Corepack,
    /// every rc file either Yarn reads a <c>yarn-path</c> or <c>yarnPath</c> from, and the script that
    /// setting named, which answers in the program's place. Both rows share the question, so this is
    /// the union of their configuration rather than either one's.
    /// </summary>
    public static ToolInputs Version(IUserEnvironment environment, string yarn, YarnScriptSetting script) =>
        Program(environment, yarn).And(ClassicConfiguration(environment)).And(BerryConfiguration(environment)).And(Named(script));

    /// <summary>What Yarn 1 reads to answer <c>yarn cache dir</c>, and the script its version answer named.</summary>
    public static ToolInputs Classic(IUserEnvironment environment, string yarn, YarnScriptSetting script) =>
        Program(environment, yarn).And(ClassicConfiguration(environment)).And(Named(script));

    /// <summary>What Berry reads to answer <c>yarn config get</c>, and the script its version answer named.</summary>
    public static ToolInputs Berry(IUserEnvironment environment, string yarn, YarnScriptSetting script) =>
        Program(environment, yarn).And(BerryConfiguration(environment)).And(Named(script));

    /// <summary>
    /// The program and what picks the Yarn behind it. The shim beside an npm-installed Yarn runs the
    /// <c>node.exe</c> beside it where there is one and the <c>node</c> on the PATH where not, and runs
    /// the Yarn whose <c>package.json</c> is in its <c>node_modules</c>; the MSI puts <c>yarn.cmd</c> in
    /// <c>Yarn\bin</c> with the <c>package.json</c> one folder up. A Corepack shim runs the release kept
    /// in Corepack's home that a <c>packageManager</c> field in the nearest <c>package.json</c>, or a
    /// <c>.corepack.env</c>, names (<a href="https://github.com/nodejs/corepack">Corepack</a>).
    /// </summary>
    private static ToolInputs Program(IUserEnvironment environment, string yarn)
    {
        var files = new List<string> { yarn };

        if (Path.GetDirectoryName(yarn) is { } beside)
        {
            files.Add(Path.Combine(beside, "node.exe"));
            files.Add(Path.Combine(beside, "node_modules", "yarn", "package.json"));

            if (Path.GetDirectoryName(beside) is { } above)
            {
                files.Add(Path.Combine(above, "package.json"));
            }
        }

        if (environment.FindExecutable("node") is { } node)
        {
            files.Add(node);
        }

        files.AddRange(ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), "package.json", ".corepack.env"));

        var corepack = NamedBy(environment, "COREPACK_HOME") is { } home
            ? home
            : Path.Combine(environment.LocalAppData, "node", "corepack");

        return new ToolInputs { Files = files, Folders = [new ListedFolder(corepack, Depth: 2)] };
    }

    /// <summary>
    /// Whether any rc file Yarn 1 reads <c>yarn-path</c> from is there, or could not be read: the files
    /// in which a Yarn 1 on the PATH can name a script Berry would not report
    /// (<see cref="YarnVersionAnswer"/>). A folder Yarn 1 reads a config from counts when it holds
    /// anything.
    /// </summary>
    public static bool MayHandYarnOnUnreported(IUserEnvironment environment)
    {
        List<string> files =
        [
            .. ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), ".yarnrc"),
            Path.Combine(environment.UserProfile, ".yarnrc"),
            Path.Combine(environment.UserProfile, ".yarn", "config"),
        ];

        if (NamedBy(environment, "YARN_CONFIG") is { } configured)
        {
            files.Add(configured);
        }

        string[] folders = [Path.Combine(environment.UserProfile, ".config", "yarn"), Path.Combine(environment.LocalAppData, "Yarn", "Config")];

        return files.Any(static file => InputStamp.Of(file).Presence is not PathPresence.Absent)
            || folders.Any(static folder => FolderEntries.Of(folder) is null or { Count: > 0 });
    }

    /// <summary>
    /// Every rc file Yarn 1 reads
    /// (<a href="https://classic.yarnpkg.com/en/docs/yarnrc">.yarnrc</a>,
    /// <a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/util/rc.js">rc.js</a>): a
    /// <c>.yarnrc</c>, with the <c>.yarnrc.yml</c> beside it, in each folder up and in the profile;
    /// <c>~\.yarn\config</c>; <c>~\.config\yarn</c> and Yarn's own <c>Config</c> folder, whichever
    /// file they hold; <c>%YARN_CONFIG%</c>; and npm's <c>.npmrc</c> files, which Yarn 1 reads for its
    /// registry settings, including the built-in ones under Node.js and <c>%PREFIX%</c>.
    /// </summary>
    private static ToolInputs ClassicConfiguration(IUserEnvironment environment)
    {
        var files = new List<string>(ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), ".yarnrc", ".npmrc", ".yarnrc.yml"))
        {
            Path.Combine(environment.UserProfile, ".yarnrc"),
            Path.Combine(environment.UserProfile, ".yarnrc.yml"),
            Path.Combine(environment.UserProfile, ".npmrc"),
            Path.Combine(environment.UserProfile, ".yarn", "config"),
        };

        if (NamedBy(environment, "YARN_CONFIG") is { } configured)
        {
            files.Add(configured);
        }

        foreach (var prefix in NodePrefixes(environment))
        {
            files.Add(Path.Combine(prefix, "etc", "yarnrc"));
            files.Add(Path.Combine(prefix, "etc", "npmrc"));
        }

        return new ToolInputs
        {
            Files = files,
            Folders =
            [
                new ListedFolder(Path.Combine(environment.UserProfile, ".config", "yarn")),
                new ListedFolder(Path.Combine(environment.LocalAppData, "Yarn", "Config")),
            ],
        };
    }

    /// <summary>
    /// Every rc file Berry reads
    /// (<a href="https://yarnpkg.com/configuration/yarnrc">.yarnrc.yml</a>): the rc file, named
    /// <c>%YARN_RC_FILENAME%</c> where that is set, in each folder up and in the profile; and the
    /// <c>yarn.lock</c> beside it, which marks a project's root.
    /// </summary>
    private static ToolInputs BerryConfiguration(IUserEnvironment environment)
    {
        var name = environment.GetToolEnvironmentVariable("YARN_RC_FILENAME") is { Length: > 0 } configured ? configured : ".yarnrc.yml";

        return new ToolInputs
        {
            Files =
            [
                .. ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), name, "yarn.lock"),
                Path.Combine(environment.UserProfile, name),
            ],
        };
    }

    /// <summary>The folders Node.js's built-in configuration is read under: Node's own, and <c>%PREFIX%</c>.</summary>
    private static IEnumerable<string> NodePrefixes(IUserEnvironment environment)
    {
        if (environment.FindExecutable("node") is { } node && Path.GetDirectoryName(node) is { } nodeFolder)
        {
            yield return nodeFolder;
        }

        if (NamedBy(environment, "PREFIX") is { } prefix)
        {
            yield return prefix;
        }
    }

    /// <summary>
    /// The file or folder the variable <paramref name="name"/> names, where Yarn, Node.js or Corepack
    /// opens it: a relative value from the folder they run in (<see cref="ToolInputs.Opened"/>). Null
    /// where the variable is unset or empty, which each of them reads as unset.
    /// </summary>
    private static string? NamedBy(IUserEnvironment environment, string name) =>
        environment.GetToolEnvironmentVariable(name) is { Length: > 0 } value ? ToolInputs.Opened(value, environment) : null;

    private static ToolInputs Named(YarnScriptSetting script) =>
        script.Script is { } file ? new ToolInputs { Files = [file] } : new ToolInputs();
}
