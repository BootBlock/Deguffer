using System.Xml;
using System.Xml.Linq;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What looking for this machine's File History target found.</summary>
public enum FileHistoryLookup
{
    /// <summary>File History has never been set up for this account, so there is nothing to find.</summary>
    NotConfigured,

    /// <summary>
    /// It is set up, and its configuration did not name a target Deguffer could use. The one case
    /// that is a limitation rather than a fact about the machine — see
    /// <see cref="FileHistoryDiscovery"/> for what is read and what is refused.
    /// </summary>
    ConfigurationUnreadable,

    /// <summary>
    /// It is set up and the target was named, and this machine's history is not there. An external
    /// drive that is unplugged is the ordinary reason, and a share that is not mounted is the other.
    /// </summary>
    TargetUnreachable,

    /// <summary>The target is named, connected, and holds this machine's saved versions.</summary>
    Found,
}

/// <summary>The outcome of one lookup, and the target where there is one.</summary>
public sealed record FileHistoryLocation(FileHistoryLookup Outcome, FileHistoryTarget? Target = null);

/// <summary>
/// Where Windows is currently sending this account's File History, read from the configuration
/// Windows keeps in the profile.
///
/// <para><b>Why not simply look for the folder.</b> Windows assigns exactly one target at a time,
/// and a machine that has used two drives keeps a complete, stale <c>FileHistory</c> folder on the
/// old one. <c>FhManagew.exe -cleanup</c> acts on the assigned target and on nothing else, so a
/// provider that sized whichever folder it happened to find first would preview one drive and trim
/// another. The configuration is the only thing on the machine that says which is which.</para>
///
/// <para><b>The schema is not documented, so nothing here depends on it.</b> No element or attribute
/// name is matched. Every leaf value in the file is taken as a candidate, the ones shaped like a
/// fully qualified path are kept, and each is confirmed by asking the disk whether this machine's
/// <c>Data</c> folder is under it. A file that names the target in an element this code has never
/// heard of is therefore read correctly, and a file that names no reachable target is declined
/// rather than guessed at.</para>
///
/// <para>Its own type rather than part of the provider, on G1: this answers "where is it", and
/// <see cref="FileHistoryProvider"/> answers "what may be done about it". It is also the half that
/// needs its own tests, because it is the half carrying the assumption about a layout nobody has
/// published.</para>
/// </summary>
public sealed class FileHistoryDiscovery(IUserEnvironment environment)
{
    /// <summary>
    /// Windows writes the configuration as a pair and swaps between them, so the name is a pattern
    /// rather than a file. The newest is read first, and an older one is only reached when the
    /// newest names nothing usable.
    /// </summary>
    private const string ConfigurationPattern = "Config*.xml";

    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private FileHistoryLocation? _located;

    /// <summary>
    /// Where the configuration lives, under this account's own profile. Its existence is what
    /// "File History is set up on this machine" means — <c>FhManagew.exe</c> ships with every
    /// Windows 11 install, including machines that have never used the feature, so the binary
    /// answers nothing.
    /// </summary>
    public string ConfigurationDirectory =>
        Path.Combine(environment.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration");

    public bool IsConfigured => LongPath.DirectoryExists(ConfigurationDirectory);

    /// <summary>
    /// The target, or why there is not one. Held for the life of a planning pass (G4): the answer
    /// costs an XML parse and a handful of existence checks, and the provider asks for it while
    /// deciding presence and again while building a plan.
    /// </summary>
    public FileHistoryLocation Locate() => _located ??= Find();

    /// <summary>Forget the answer, so a drive plugged in while the app was open is seen.</summary>
    public void Invalidate() => _located = null;

    private FileHistoryLocation Find()
    {
        if (!IsConfigured)
        {
            return new FileHistoryLocation(FileHistoryLookup.NotConfigured);
        }

        var named = false;

        foreach (var candidate in ConfiguredRoots())
        {
            named = true;

            var target = new FileHistoryTarget(candidate, environment.UserName, environment.MachineName);

            if (LongPath.DirectoryExists(target.DataDirectory))
            {
                return new FileHistoryLocation(FileHistoryLookup.Found, target);
            }
        }

        return new FileHistoryLocation(
            named ? FileHistoryLookup.TargetUnreachable : FileHistoryLookup.ConfigurationUnreadable);
    }

    /// <summary>
    /// Every target device the configuration could be naming, newest configuration first and in
    /// document order within each, deduplicated so a value repeated across the pair is probed once.
    /// </summary>
    private IEnumerable<string> ConfiguredRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in ConfigurationFiles())
        {
            foreach (var value in LeafValuesIn(file))
            {
                if (TargetRootOf(value) is { } root && seen.Add(root))
                {
                    yield return root;
                }
            }
        }
    }

    private IEnumerable<string> ConfigurationFiles()
    {
        DirectoryInfo directory;

        try
        {
            directory = new DirectoryInfo(LongPath.Extended(ConfigurationDirectory));

            // Materialised, not streamed: the sort has to see every entry anyway, and the count is
            // the two or three files Windows keeps here.
            return [.. directory
                .EnumerateFiles(ConfigurationPattern)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A refusal is ordinary (§5.3), and it is indistinguishable from an unreadable
            // configuration as far as the user's next move goes.
            return [];
        }
    }

    /// <summary>
    /// Every value in the file that could be a path: the text of each element that holds no
    /// elements of its own, and every attribute value. Element names are deliberately not consulted
    /// — see the class remarks.
    /// </summary>
    private static IEnumerable<string> LeafValuesIn(string file) =>
        Load(file) is { } document ? document.Descendants().SelectMany(ValuesOf) : [];

    /// <summary>
    /// A container element's own text is the concatenation of everything below it, which is never a
    /// path, so only a leaf contributes one.
    /// </summary>
    private static IEnumerable<string> ValuesOf(XElement element)
    {
        var attributes = element.Attributes().Select(attribute => attribute.Value);

        return element.HasElements ? attributes : attributes.Append(element.Value);
    }

    private static XDocument? Load(string file)
    {
        try
        {
            // From a stream, not from the path: the overload taking a string treats it as a URI, and
            // §6.3's extended-length form is not one — every configuration would fail to parse on a
            // path that reached this in the shape the rest of Core uses.
            //
            // XDocument prohibits DTD processing by default either way, so a configuration file
            // somebody had replaced cannot pull in an external entity.
            using var stream = File.OpenRead(LongPath.Extended(file));

            return XDocument.Load(stream, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The target device <paramref name="value"/> names, or null where it is not a path at all.
    ///
    /// <para>The configuration may name the device, or the <c>FileHistory</c> folder on it, or this
    /// account's folder, or this machine's. All four are reduced to the device, so the rest of this
    /// type has one shape to reason about and <see cref="FileHistoryTarget"/> builds the same four
    /// folders whichever arrived.</para>
    /// </summary>
    private string? TargetRootOf(string value)
    {
        if (Normalise(value) is not { } path)
        {
            return null;
        }

        foreach (var suffix in DescendingSuffixes())
        {
            if (Trim(path, suffix) is { } trimmed)
            {
                return trimmed;
            }
        }

        return path;
    }

    /// <summary>Longest first, so a full path is not left holding the <c>FileHistory</c> folder.</summary>
    private IEnumerable<string> DescendingSuffixes() =>
    [
        Path.Combine("FileHistory", environment.UserName, environment.MachineName),
        Path.Combine("FileHistory", environment.UserName),
        "FileHistory",
    ];

    /// <summary>
    /// <paramref name="path"/> without <paramref name="suffix"/>, or null where it does not end
    /// with it. The separator is part of the test, so a folder called <c>MyFileHistory</c> is not
    /// mistaken for one called <c>FileHistory</c>.
    /// </summary>
    private static string? Trim(string path, string suffix)
    {
        var ending = Path.DirectorySeparatorChar + suffix;

        return path.EndsWith(ending, StringComparison.OrdinalIgnoreCase)
            ? path[..^ending.Length]
            : null;
    }

    /// <summary>
    /// <paramref name="value"/> as a fully qualified path with no trailing separator, or null where
    /// it is not one.
    ///
    /// <para>A drive with no letter is named in the device namespace, and that is the one form
    /// <see cref="LongPath.Configured"/> cannot handle: it strips the prefix before normalising, and
    /// <c>Volume{…}\</c> is not a qualified path, so what is left resolves against Deguffer's own
    /// working directory — a folder nobody pointed at, silently. Such a value is therefore kept as
    /// it stands, and refused outright if it carries a relative segment, because the device
    /// namespace resolves nothing and a <c>..</c> in one would reach a folder nobody named.</para>
    ///
    /// <para><b>Only half of this is provable in a fixture.</b> The refusal above is, and
    /// <c>FileHistoryDiscoveryTests</c> covers it. The volume-GUID case is not, because a test
    /// cannot create a volume — so what stands behind that branch is this paragraph rather than an
    /// assertion, and a test written against <c>\\?\C:\…</c> would pass with the branch deleted.</para>
    /// </summary>
    private static string? Normalise(string value)
    {
        var trimmed = value.Trim();

        if (!trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return LongPath.Configured(trimmed);
        }

        var segments = trimmed.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        return segments.Contains(".") || segments.Contains("..")
            ? null
            : Path.TrimEndingDirectorySeparator(trimmed);
    }
}
