using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// One plugin folder in Pulumi's plugin cache, named as Pulumi names it: <c>&lt;kind&gt;-&lt;name&gt;-v&lt;version&gt;</c>.
///
/// <para><b>Pulumi's own rule, and a narrower one for the version.</b> The kind, name and version are
/// split by the expression Pulumi lists its cache with (<c>pluginRegexp</c> in
/// <c>sdk/go/common/workspace/plugins.go</c>), and the kind must be one Pulumi knows. Pulumi then
/// reads the version leniently, so <c>v1.0</c> and <c>v1.0.0</c> name the same plugin to it. This
/// recognises only a version written exactly as Pulumi writes the folders it installs, so the text
/// in the name is the text <c>pulumi plugin rm</c> is given, and the folder is the plugin the command
/// means. Anything else is Tier 4.</para>
///
/// <para><b>No <c>x</c> anywhere in the version.</b> <c>plugin rm</c> reads its version argument as a
/// range, and the range parser (<c>blang/semver</c> 3.5.1, <c>expandWildcardVersion</c>) takes any
/// argument holding a lower-case <c>x</c> as a wildcard, so a build tag such as <c>+windows-x64</c>
/// would ask for something other than the plugin.</para>
/// </summary>
/// <param name="Folder">The plugin's folder, in display form.</param>
/// <param name="Kind">What kind of plugin it is, such as <c>resource</c>.</param>
/// <param name="Name">The plugin's name, such as <c>aws</c>.</param>
/// <param name="Version">The version as the folder's name writes it, without the <c>v</c>.</param>
internal sealed record PulumiPlugin(string Folder, string Kind, string Name, string Version)
{
    /// <summary>The kinds Pulumi accepts (<c>apitype.IsPluginKind</c>), each as its folders spell it.</summary>
    public static readonly IReadOnlySet<string> Kinds =
        new HashSet<string>(["resource", "language", "analyzer", "converter", "tool"], StringComparer.Ordinal);

    /// <summary>The files Pulumi keeps beside a plugin's folder, named by adding these to the folder's name.</summary>
    public static readonly IReadOnlyList<string> SidecarSuffixes = [LockSuffix, LastUsedSuffix, PartialSuffix];

    /// <summary>Held while Pulumi installs the plugin.</summary>
    public const string LockSuffix = ".lock";

    /// <summary>Written each time Pulumi starts the plugin. Its time is the plugin's last use.</summary>
    public const string LastUsedSuffix = ".lastused";

    /// <summary>Present until Pulumi has finished installing the plugin, which Pulumi does not list until then.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>
    /// <c>pluginRegexp</c>, which is case-sensitive. The name's character class in Go,
    /// <c>[a-zA-Z0-9-_.]</c>, is a range and three literals, written here with the hyphen last.
    /// </summary>
    private static readonly Regex FolderName = new(
        "^(?<kind>[a-z]+)-(?<name>[a-zA-Z0-9-][a-zA-Z0-9_.-]*[a-zA-Z0-9])-v(?<version>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// A version Pulumi's strict parse accepts and writes back unchanged: no leading zeros, no number too
    /// long for 64 bits, and identifiers of the characters it allows.
    /// </summary>
    private static readonly Regex CanonicalVersion = new(
        @"^(0|[1-9][0-9]{0,17})\.(0|[1-9][0-9]{0,17})\.(0|[1-9][0-9]{0,17})"
        + @"(-(0|[1-9][0-9]{0,17}|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9][0-9]{0,17}|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*)?"
        + @"(\+[0-9a-zA-Z-]+(\.[0-9a-zA-Z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// The folders an older Pulumi extracted a plugin into before moving it into place
    /// (<c>installingPluginRegexp</c>), which Pulumi never lists.
    /// </summary>
    private static readonly Regex Installing = new(@"\.tmp[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>The arguments of <c>pulumi plugin rm</c> that name this plugin.</summary>
    public string RemoveArguments => $"plugin rm {Kind} {Name} {Version} --yes --non-interactive";

    /// <summary>The plugin's own name for the user, as Pulumi prints it: <c>resource aws 6.0.0</c>.</summary>
    public string Title => $"{Kind} {Name} {Version}";

    /// <summary>The path of one of the files Pulumi keeps beside the folder.</summary>
    public string Sidecar(string suffix) => Folder + suffix;

    /// <summary>
    /// The plugin a folder is, where its name is one this recognises, or null. Says nothing about
    /// whether the entry is a folder: the caller has the listing.
    /// </summary>
    /// <param name="folder">The folder, in display form.</param>
    public static PulumiPlugin? Recognise(string folder) =>
        Read(Path.GetFileName(folder)) is { } name && CanonicalVersion.IsMatch(name.Version) && !name.Version.Contains('x', StringComparison.Ordinal)
            ? new PulumiPlugin(folder, name.Kind, name.Name, name.Version)
            : null;

    /// <summary>
    /// Whether <c>pulumi plugin rm</c> for this plugin could also remove the folder named
    /// <paramref name="entry"/>, by Pulumi's own reading of folder names. A version this cannot read as
    /// Pulumi does counts, because Pulumi may read it as this one. Pulumi compares versions ignoring
    /// what follows a <c>+</c>, so two folders differing only there are the same plugin to it.
    /// </summary>
    /// <param name="entry">The name of a folder beside this one.</param>
    public bool CouldAlsoRemove(string entry)
    {
        if (entry.Equals(Path.GetFileName(Folder), StringComparison.OrdinalIgnoreCase)
            || Read(entry) is not { } other
            || other.Kind != Kind
            || other.Name != Name)
        {
            return false;
        }

        return !CanonicalVersion.IsMatch(other.Version) || Precedence(other.Version) == Precedence(Version);
    }

    /// <summary>The version without its build metadata, which is the part Pulumi compares.</summary>
    private static string Precedence(string version) =>
        version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0 ? version[..plus] : version;

    private static (string Kind, string Name, string Version)? Read(string entry)
    {
        if (Installing.IsMatch(entry) || FolderName.Match(entry) is not { Success: true } match)
        {
            return null;
        }

        var kind = match.Groups["kind"].Value;

        return Kinds.Contains(kind) ? (kind, match.Groups["name"].Value, match.Groups["version"].Value) : null;
    }
}
