using System.Text.Json;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A machine with Yarn installed through npm, as the tests of what Yarn's two rows remember see it:
/// a real <c>yarn.cmd</c>, the Node.js it runs on, and a fake Yarn whose answers a test changes
/// between passes, each counted (#447).
///
/// <para>Every ask starts after the fixture was written, as a real one starts long after a
/// configuration file was last saved, so the memory keeps what it is told.</para>
/// </summary>
internal sealed class YarnToolFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new();

    public YarnToolFixture()
    {
        Environment = new FakeUserEnvironment(_temp.Path);
        System = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        Memory = new ToolAnswerMemory(_clock);

        Yarn = Write("AppData", "Roaming", "npm", "yarn.cmd");
        Write("AppData", "Roaming", "npm", "node_modules", "yarn", "package.json");
        Environment.WithExecutable("yarn", Yarn).WithExecutable("node", Write("nodejs", "node.exe"));
        Directory.CreateDirectory(Path.Combine(Environment.LocalAppData, "node", "corepack", "yarn"));
        Directory.CreateDirectory(ToolInputs.WorkingDirectory(Environment));
        Script = Write("project", ".yarn", "releases", "yarn.cjs");

        Runner = new FakeProcessRunner().Replying(Yarn, Reply);

        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    public FakeUserEnvironment Environment { get; }

    public FakeSystemDirectories System { get; }

    public ToolAnswerMemory Memory { get; }

    public FakeProcessRunner Runner { get; }

    /// <summary>The <c>yarn</c> on the PATH, a real file so replacing it can be noticed.</summary>
    public string Yarn { get; }

    /// <summary>The script Yarn says its <c>yarn-path</c> or <c>yarnPath</c> names.</summary>
    public string Script { get; }

    public string Temp => _temp.Path;

    /// <summary>What <c>yarn -v</c> prints.</summary>
    public string Version { get; set; } = "1.22.22";

    /// <summary>Yarn 1's cache root; <c>yarn cache dir</c> prints its <c>v6</c>.</summary>
    public string? ClassicRoot { get; set; }

    /// <summary>The global folder Berry reports.</summary>
    public string? BerryGlobalFolder { get; set; }

    /// <summary>Whether the location question (<c>cache dir</c> or <c>config get globalFolder</c>) fails.</summary>
    public bool LocationFails { get; set; }

    /// <summary>Whether the script question fails.</summary>
    public bool ScriptFails { get; set; }

    /// <summary>What a Yarn 1 clean does when it runs, beyond being recorded.</summary>
    public Action<string>? OnClean { get; set; }

    public int Asked(string arguments) => Runner.Invocations.Count(i => i.Arguments == arguments);

    public void Dispose() => _temp.Dispose();

    /// <summary>A small file at <paramref name="segments"/> under the fake profile.</summary>
    public string Write(params string[] segments) => WriteAt(Environment.UserProfile, segments);

    public static string WriteAt(string folder, params string[] segments)
    {
        var path = Path.Combine([folder, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>
    /// Change the input at <paramref name="relative"/> under the profile: write it where it is there,
    /// and create it where not.
    /// </summary>
    public void Change(string relative)
    {
        var path = Path.Combine(Environment.UserProfile, relative);

        if (File.Exists(path))
        {
            File.AppendAllText(path, "changed");
        }
        else
        {
            Write(relative.Split(Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Replace the program with another at the same path, as an upgrade does.</summary>
    public void ReplaceYarn()
    {
        var replacement = Path.Combine(_temp.Path, "yarn.cmd.new");
        File.WriteAllText(replacement, "@echo a newer yarn");
        File.Move(replacement, Yarn, overwrite: true);
    }

    /// <summary>
    /// Copy another release over the script <c>yarn-path</c> or <c>yarnPath</c> names, every rc file
    /// left as it was, and have it answer <c>yarn -v</c> as <paramref name="version"/>.
    /// </summary>
    public void ReplaceScript(string version)
    {
        var replacement = Path.Combine(_temp.Path, "yarn.cjs.new");
        File.WriteAllText(replacement, $"// Yarn {version}");
        File.Move(replacement, Script, overwrite: true);
        Version = version;
    }

    private CommandOutcome Reply(string arguments)
    {
        switch (arguments)
        {
            case "-v":
                return Answer(Version);
            case "cache dir":
                return LocationFails ? Failed() : Answer(Path.Combine(ClassicRoot!, "v6"));
            case "config get yarn-path":
                return ScriptFails ? Failed() : Answer(Script);
            case "config get globalFolder --json":
                return LocationFails ? Failed() : Answer(JsonSerializer.Serialize(BerryGlobalFolder));
            case "config get enableCacheClean --json":
                return Answer("true");
            case "config get yarnPath --json":
                return ScriptFails ? Failed() : Answer(JsonSerializer.Serialize(Script));
        }

        if (OnClean is null)
        {
            throw new Xunit.Sdk.XunitException($"Yarn was asked '{arguments}'.");
        }

        OnClean(arguments);
        return Answer("success Cleared cache.");
    }

    private static CommandOutcome Answer(string line) => new(0, line + "\n", string.Empty);

    private static CommandOutcome Failed() => new(1, string.Empty, "error An unexpected error occurred.\n");
}
