using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything <c>poetry config cache-dir</c> and <c>poetry config virtualenvs.path</c> read to say
/// where Poetry keeps things, so <see cref="ToolAnswerMemory"/> asks again the moment any of it
/// changes (#447).
///
/// <para><b>Configuration.</b> Poetry's <c>Config.create</c> merges <c>config.toml</c> and then
/// <c>auth.toml</c> from its configuration folder: <c>%POETRY_CONFIG_DIR%</c>, or platformdirs' roaming
/// configuration folder, <c>%APPDATA%\pypoetry</c> (<c>poetry/locations.py</c>). <c>poetry config</c>
/// then merges the <c>poetry.toml</c> beside the <c>pyproject.toml</c> it finds searching up from the
/// folder it runs in, so both names are declared in that folder and every one above it: creating a
/// <c>pyproject.toml</c> higher up moves which <c>poetry.toml</c> is read.</para>
///
/// <para><b>Variables.</b> Any <c>POETRY_&lt;KEY&gt;</c> overrides a setting, and the whole block
/// is in the fingerprint, so none needs an entry.</para>
///
/// <para><b>Code.</b> The executable, so a Poetry found at the same path after a reinstall is asked
/// again.</para>
/// </summary>
internal static class PoetryInputs
{
    private static readonly string[] ProjectFiles = ["pyproject.toml", "poetry.toml"];

    /// <param name="poetry">The executable asked.</param>
    /// <param name="environment">The account whose roaming profile holds Poetry's configuration.</param>
    /// <param name="variables">Every variable Poetry is started with, which may move its configuration folder.</param>
    public static ToolInputs Of(string poetry, IUserEnvironment environment, IReadOnlyDictionary<string, string> variables)
    {
        var workingDirectory = ToolInputs.WorkingDirectory(environment);
        List<string> files = [poetry, .. ToolInputs.InEachFolderUp(workingDirectory, ProjectFiles)];

        if (ConfigurationFolder(environment, variables, workingDirectory) is { } folder)
        {
            files.Add(Path.Combine(folder, "config.toml"));
            files.Add(Path.Combine(folder, "auth.toml"));
        }

        return new ToolInputs { Files = files };
    }

    /// <summary>
    /// The folder Poetry reads <c>config.toml</c> and <c>auth.toml</c> from, or null where
    /// <c>POETRY_CONFIG_DIR</c> names nothing Windows could open.
    ///
    /// <para>Poetry takes the variable as it stands where it is set and not empty (<c>os.getenv(...) or
    /// default</c>), with no <c>~</c> expanded, so a relative value is read from the folder it runs
    /// in.</para>
    /// </summary>
    private static string? ConfigurationFolder(
        IUserEnvironment environment,
        IReadOnlyDictionary<string, string> variables,
        string workingDirectory)
    {
        if (!variables.TryGetValue("POETRY_CONFIG_DIR", out var named) || named.Length == 0)
        {
            return Path.Combine(environment.RoamingAppData, "pypoetry");
        }

        try
        {
            return LongPath.Configured(Path.GetFullPath(named, workingDirectory));
        }
        catch (ArgumentException)
        {
            // A NUL in the value, which no path can hold, so Poetry's open of it fails too.
            return null;
        }
    }
}
