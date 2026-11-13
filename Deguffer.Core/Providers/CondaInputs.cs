using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything <c>conda info --json</c> reads to say where conda keeps things, so
/// <see cref="ToolAnswerMemory"/> asks again the moment any of it changes (#447).
///
/// <para><b>Configuration.</b> conda's search path on Windows (<c>SEARCH_PATH</c> in
/// <c>conda/base/constants.py</c>): in each of <c>C:\ProgramData\conda</c>, <c>%CONDA_ROOT%</c>,
/// <c>%XDG_CONFIG_HOME%\conda</c>, <c>~\.config\conda</c>, <c>~\.conda</c> and the target prefix
/// (the installation, or <c>%CONDA_PREFIX%</c>), a <c>.condarc</c>, a <c>condarc</c> and a
/// <c>condarc.d</c>; then <c>~\.condarc</c> and <c>%CONDARC%</c>. conda reads any entry that is a
/// folder as the YAML files in it, whatever its name, so every entry is declared as a listed folder,
/// whose own stamp covers the entry being a file. An entry whose variable is unset is left out: setting
/// it later changes the environment block the answer is compared by.</para>
///
/// <para><b>Code.</b> The executable, and the installation's <c>conda-meta</c> listing, which gains or
/// rewrites an entry whenever conda or any package in the base environment is updated, so a newer
/// conda's answer is never read as the older one's.</para>
/// </summary>
internal static class CondaInputs
{
    private static readonly string[] SearchedNames = [".condarc", "condarc", "condarc.d"];

    /// <param name="conda">The executable asked.</param>
    /// <param name="installation">conda's answer, whose prefix is itself on the search path.</param>
    /// <param name="environment">The account whose profile conda searches.</param>
    /// <param name="programData">The machine-wide data folder, where the system configuration is.</param>
    /// <param name="variables">Every variable conda is started with, which the search path reads.</param>
    public static ToolInputs Of(
        string conda,
        CondaInstallation? installation,
        IUserEnvironment environment,
        string programData,
        IReadOnlyDictionary<string, string> variables)
    {
        var root = installation?.RootPrefix;
        var profile = environment.UserProfile;

        // Each folder conda looks for SearchedNames in, as the template conda expands: a variable's
        // value, then the rest of the entry.
        (string? Value, string Under)[] searched =
        [
            (Path.Combine(programData, "conda"), string.Empty),
            (Value(variables, "CONDA_ROOT"), string.Empty),
            (Value(variables, "XDG_CONFIG_HOME"), "conda"),
            (Path.Combine(profile, ".config", "conda"), string.Empty),
            (Path.Combine(profile, ".conda"), string.Empty),
            (root, string.Empty),
            (Value(variables, "CONDA_PREFIX"), string.Empty),
        ];

        var entries = searched
            .Where(folder => folder.Value is not null)
            .SelectMany(_ => SearchedNames, (folder, name) => Entry(folder.Value!, Path.Join(folder.Under, name), environment))
            .Append(Path.Combine(profile, ".condarc"));

        if (Value(variables, "CONDARC") is { } named)
        {
            entries = entries.Append(Entry(named, string.Empty, environment));
        }

        if (root is not null)
        {
            entries = entries.Append(Path.Combine(root, "conda-meta"));
        }

        return new ToolInputs
        {
            Files = [conda],
            Folders =
            [
                .. entries
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new ListedFolder(entry)),
            ],
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// The path conda opens for a search-path entry starting with <paramref name="value"/>, or null
    /// where it names nothing Windows could open.
    ///
    /// <para>conda's <c>_expand_search_path</c> substitutes the variable once (a variable inside its value
    /// is left as written), then calls <c>Path.expanduser</c>, and opens the result as it stands, so a
    /// relative one is read from the folder conda runs in, and one starting at a separator from the
    /// root of that folder's drive.</para>
    /// </summary>
    private static string? Entry(string value, string rest, IUserEnvironment environment)
    {
        var path = rest.Length == 0 ? value : value + Path.DirectorySeparatorChar + rest;

        if (path == "~" || path.StartsWith(@"~\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = environment.UserProfile + path[1..];
        }

        try
        {
            return LongPath.Configured(Path.GetFullPath(path.Length == 0 ? "." : path, ToolInputs.WorkingDirectory(environment)));
        }
        catch (ArgumentException)
        {
            // A NUL in the value, which no path can hold, so conda's open of it fails too.
            return null;
        }
    }
}
