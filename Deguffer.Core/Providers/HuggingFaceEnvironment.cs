using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>hf env</c> says about the <c>huggingface_hub</c> it belongs to: its version, and the
/// folders it resolved from its variables.
///
/// <para><b>Asked rather than worked out.</b> The hub cache moves with <c>HF_HUB_CACHE</c>, then
/// <c>HUGGINGFACE_HUB_CACHE</c>, then <c>HF_HOME\hub</c>, and <c>HF_HOME</c> follows
/// <c>XDG_CACHE_HOME</c>, each through Python's <c>expanduser</c> and <c>expandvars</c>
/// (<c>constants.py</c>). <c>hf env</c> prints what that came to, and every command after it is
/// told the same folder with <c>--cache-dir</c>, so what the plan examined is what the command
/// acts on.</para>
///
/// <para><b>Read from the text, because that is all it gives.</b> <c>hf env</c> has no
/// machine-readable form in any version: it prints <c>- NAME: value</c> lines meant to be pasted
/// into a bug report, and the same lines from 0.x to 2.x. A line that is missing reads as nothing
/// said, never as a default.</para>
/// </summary>
/// <param name="Version">The version printed, or null where none could be read.</param>
/// <param name="HubCache">The hub cache as printed, or null where it was not.</param>
/// <param name="Credentials">
/// The files <c>huggingface_hub</c> keeps the user's access tokens in, where it named them.
/// </param>
/// <param name="Assets">The folder libraries keep their own downloads in, where it named it.</param>
internal sealed partial record HuggingFaceEnvironment(
    Version? Version,
    string? HubCache,
    IReadOnlyList<string> Credentials,
    string? Assets)
{
    /// <summary>
    /// The first version whose removal knows about the shared blob store. From 1.32 a repository's
    /// files can be links into <c>hub\blobs</c>; the vendor's guide says <c>huggingface_hub</c> 1.31
    /// and older leave a removed repository's shared files in the store, and that other tools can
    /// remove a shared file another repository still uses.
    /// </summary>
    public static readonly Version SharedBlobsVersion = new(1, 32);

    /// <summary>
    /// Whether this version's <c>hf cache rm</c> removes a repository's shared files and keeps those
    /// another repository uses.
    /// </summary>
    public bool KnowsSharedBlobs => Version is { } version && version >= SharedBlobsVersion;

    public static HuggingFaceEnvironment Read(string standardOutput)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match line in Line().Matches(standardOutput))
        {
            values.TryAdd(line.Groups["name"].Value, line.Groups["value"].Value.Trim());
        }

        return new HuggingFaceEnvironment(
            values.TryGetValue("huggingface_hub version", out var printed) ? ReadVersion(printed) : null,
            Folder(values, "HF_HUB_CACHE"),
            [.. new[] { Folder(values, "HF_TOKEN_PATH"), Folder(values, "HF_STORED_TOKENS_PATH") }.OfType<string>()],
            Folder(values, "HF_ASSETS_CACHE"));
    }

    /// <summary>
    /// The major and minor version at the front of what was printed, so a release candidate or a
    /// development build of 1.32 reads as 1.32.
    /// </summary>
    private static Version? ReadVersion(string printed) =>
        LeadingVersion().Match(printed) is { Success: true } match
        && int.TryParse(match.Groups["major"].Value, out var major)
        && int.TryParse(match.Groups["minor"].Value, out var minor)
            ? new Version(major, minor)
            : null;

    /// <summary>
    /// A value that names a full path, in Deguffer's form. The tool prints the folder it joined with
    /// forward slashes and the part it added with a backslash, so one path can carry both.
    /// </summary>
    private static string? Folder(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && LongPath.Configured(value) is { } folder ? LongPath.Display(folder) : null;

    // "- HF_HUB_CACHE: C:/Users/<user>/.cache/huggingface\hub": the value is the rest of the line. Python
    // writes a pipe in text mode, so a line ends "\r\n" on Windows, and Multiline's "$" is before "\n" alone.
    [GeneratedRegex(@"^- (?<name>[^:\r\n]+): (?<value>[^\r\n]*)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    [GeneratedRegex(@"\A(?<major>[0-9]{1,6})\.(?<minor>[0-9]{1,6})", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersion();
}
