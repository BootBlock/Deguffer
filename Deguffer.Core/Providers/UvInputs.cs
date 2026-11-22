using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything <c>uv cache dir</c>, <c>uv tool dir</c> and <c>uv python dir</c> read to say where uv
/// keeps its cache, its tools and its interpreters, so <see cref="ToolAnswerMemory"/> asks again the
/// moment any of it changes (#1059). One declaration serves the three questions: each reads a part of
/// it, and a change to any part asks all three again.
///
/// <para><b>Configuration.</b> uv reads the <c>uv.toml</c>, or the <c>pyproject.toml</c> with a
/// <c>[tool.uv]</c> table, nearest the folder it runs in, searching every folder up to the drive root,
/// so both names are declared in each of them: a file created higher up can be the nearest one. It
/// then reads <c>uv.toml</c> in <c>%APPDATA%\uv</c>, <c>%SYSTEMDRIVE%\ProgramData\uv\uv.toml</c>, and
/// the file <c>UV_CONFIG_FILE</c> names. uv takes <c>APPDATA</c> and <c>SYSTEMDRIVE</c> from the
/// variables it is started with, and ignores <c>XDG_CONFIG_HOME</c> on Windows (observed with uv
/// 0.11).</para>
///
/// <para><b>State.</b> uv keeps its tools and interpreters in <c>%APPDATA%\uv\data</c> where that
/// folder exists, and in <c>%APPDATA%\uv</c> otherwise (<see cref="UvStorage"/>), so the legacy folder
/// is stamped: creating or removing it moves both answers.</para>
///
/// <para><b>Variables.</b> <c>UV_CACHE_DIR</c>, <c>UV_TOOL_DIR</c>, <c>UV_PYTHON_INSTALL_DIR</c>,
/// <c>UV_NO_CONFIG</c> and <c>LOCALAPPDATA</c> each move an answer, and the whole block is in the
/// fingerprint, so none needs an entry.</para>
///
/// <para><b>Code.</b> The executable, so a uv updated in place is asked again.</para>
/// </summary>
internal static class UvInputs
{
    private static readonly string[] ProjectFiles = ["uv.toml", "pyproject.toml"];

    /// <param name="uv">The executable asked.</param>
    /// <param name="environment">The account whose roaming profile holds uv's configuration and state.</param>
    /// <param name="variables">Every variable uv is started with.</param>
    public static ToolInputs Of(string uv, IUserEnvironment environment, IReadOnlyDictionary<string, string> variables)
    {
        List<string> files = [uv, .. ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), ProjectFiles)];

        foreach (var roaming in RoamingRoots(environment, variables))
        {
            files.Add(Path.Combine(roaming, "uv", "uv.toml"));
            files.Add(UvStorage.LegacyStateRoot(Path.Combine(roaming, "uv")));
        }

        if (Named(variables, "SYSTEMDRIVE") is { } drive
            && ToolInputs.Opened(drive.TrimEnd('\\', '/') + @"\ProgramData\uv\uv.toml", environment) is { } system)
        {
            files.Add(system);
        }

        if (Named(variables, "UV_CONFIG_FILE") is { } named && ToolInputs.Opened(named, environment) is { } configFile)
        {
            files.Add(configFile);
        }

        return new ToolInputs { Files = files };
    }

    /// <summary>
    /// The roaming profile uv reads, which is the <c>APPDATA</c> it is started with, and the account's
    /// own roaming folder as well: what uv falls back to without the variable is undocumented, and a
    /// path declared in vain costs one attribute read.
    /// </summary>
    private static IEnumerable<string> RoamingRoots(IUserEnvironment environment, IReadOnlyDictionary<string, string> variables)
    {
        var own = environment.RoamingAppData;
        yield return own;

        if (Named(variables, "APPDATA") is { } named
            && ToolInputs.Opened(named, environment) is { } roaming
            && !roaming.Equals(own, StringComparison.OrdinalIgnoreCase))
        {
            yield return roaming;
        }
    }

    private static string? Named(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}
