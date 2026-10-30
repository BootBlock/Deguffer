using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Scoop install laid out as Scoop's installer lays it out, under a fake profile: the
/// <c>scoop.cmd</c> shim whose first line names the <c>scoop.ps1</c> it runs, and the folders the
/// cache sits beside.
/// </summary>
internal sealed class ScoopFixture : IDisposable
{
    /// <summary>A download as Scoop's newer <c>cache_path</c> names it.</summary>
    public const string Download = "git#2.47.1#8a3f1c2.7z";

    private readonly TempDirectory _temp = new();

    public ScoopFixture()
    {
        Environment = new FakeUserEnvironment(_temp.Path);
        Runner = new FakeProcessRunner();
    }

    public FakeUserEnvironment Environment { get; }

    /// <summary>Has no reply set, so anything the plan starts throws, and is recorded either way.</summary>
    public FakeProcessRunner Runner { get; }

    /// <summary>Scoop's root where nothing has moved it.</summary>
    public string DefaultRoot => Path.Combine(Environment.UserProfile, "scoop");

    public string DefaultCache => Path.Combine(DefaultRoot, "cache");

    public string UserConfig => Path.Combine(Environment.UserProfile, ".config", "scoop", "config.json");

    /// <summary>A folder under the fixture, outside the profile, that no default names.</summary>
    public string Elsewhere(params string[] segments) => Path.Combine([_temp.Path, "elsewhere", .. segments]);

    /// <summary>
    /// Install Scoop at <paramref name="root"/>, the default root unless given, with the shim the
    /// installer writes, and put the shim on the fake <c>PATH</c>. Returns the shim.
    /// </summary>
    public string Install(string? root = null, string? found = null)
    {
        root ??= DefaultRoot;

        var shim = Path.Combine(root, "shims", ScoopCommand.FileName);
        WriteText(shim, string.Join(
            "\r\n",
            $@"@rem {root}\apps\scoop\current\bin\scoop.ps1",
            "@echo off",
            "setlocal enabledelayedexpansion",
            "set args=%*"));

        foreach (var folder in new[] { "apps", "buckets", "persist" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }

        Environment.WithExecutable("scoop", found ?? shim);
        return shim;
    }

    /// <summary>Write a file of <paramref name="bytes"/> into <paramref name="folder"/>.</summary>
    public string File(string folder, string name, int bytes = 4096)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }

    public ScoopCacheProvider Provider() => new(Environment, Runner, FakeProcessInspector.NothingRunning);

    /// <summary>The plan's one step, asserted to be the command and nothing else.</summary>
    public static RunCommandStep Step(CleanupPlan plan) => Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

    public void Dispose() => _temp.Dispose();
}
