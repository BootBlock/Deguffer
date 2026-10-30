using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Conan's answers to <c>conan config home</c> and <c>conan config show core.cache:storage_path</c>,
/// read from what each prints on standard output. Both are in
/// <c>conan/cli/commands/config.py</c>, Conan 2.34.0 (commit <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>),
/// and print through <c>cli_out_write</c> in <c>conan/api/output.py</c>, which writes the value and
/// a line break. Conan's own messages go to standard error.
/// </summary>
internal static partial class ConanConfigOutput
{
    /// <summary>The folder in the home that holds the package store, unless the setting below moves it.</summary>
    public const string DefaultStoreFolder = "p";

    /// <summary>The setting that moves the package store out of the home.</summary>
    public const string StoragePathSetting = "core.cache:storage_path";

    /// <summary>
    /// The home <c>conan config home</c> printed, in full form, or null where it printed anything but
    /// one full path. The command returns <c>ConanAPI.cache_folder</c>, the same value every other
    /// command works in.
    /// </summary>
    public static string? ReadHome(string standardOutput) =>
        Lines(standardOutput) is [var only] ? LongPath.Configured(only) : null;

    /// <summary>
    /// The store <c>PkgCache</c> in <c>conan/internal/cache/cache.py</c> opens:
    /// <c>global_conf.get("core.cache:storage_path") or os.path.join(cache_folder, "p")</c>, made
    /// absolute against the folder Conan runs in.
    ///
    /// <para><c>config show</c> prints only settings the user defined, each as <c>name: value</c> with
    /// the name coloured where <c>CLICOLOR_FORCE</c> asks for colour, so no line at all means the
    /// default. A value of <c>None</c> is a setting unset with <c>=!</c>, or the literal word, which
    /// Conan's parser also reads as nothing, and Conan then falls back to the default too.</para>
    ///
    /// <para>Null where the answer is anything else: a relative value would be resolved against
    /// Deguffer's own tool folder, and a reply this cannot read names no store at all.</para>
    /// </summary>
    public static string? ReadStore(string standardOutput, string home)
    {
        var lines = Lines(Colour().Replace(standardOutput, string.Empty));

        if (lines.Count == 0)
        {
            return Path.Combine(home, DefaultStoreFolder);
        }

        if (lines is not [var only] || !only.StartsWith(StoragePathSetting + ": ", StringComparison.Ordinal))
        {
            return null;
        }

        var value = only[(StoragePathSetting.Length + 2)..];

        return value == "None" ? Path.Combine(home, DefaultStoreFolder) : LongPath.Configured(value);
    }

    private static IReadOnlyList<string> Lines(string standardOutput) =>
        standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The SGR sequences colorama writes around a coloured name.</summary>
    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Colour();
}
