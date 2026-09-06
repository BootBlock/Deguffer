using System.Xml.Linq;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What looking for this machine's File History target found.</summary>
public enum FileHistoryLookup
{
    /// <summary>File History has never been set up for this account, so there is nothing to find.</summary>
    NotConfigured,

    /// <summary>
    /// It is set up, and this machine's saved versions are not under anything its configuration
    /// names. An unplugged external drive is the ordinary reason; a configuration Deguffer could not
    /// read is the other.
    ///
    /// <para><b>One outcome rather than two, because the parse cannot tell them apart.</b> No
    /// element name is matched, so "the configuration named a target Deguffer cannot reach" and "it
    /// named no target at all" both arrive here as "no candidate held the folder" — a real
    /// configuration carries absolute paths for the protected folders as well, and those are
    /// candidates too. Reporting "the drive is not connected" off that evidence would be a specific
    /// claim about the machine that nothing established.</para>
    /// </summary>
    TargetNotFound,

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

        foreach (var candidate in ConfiguredRoots())
        {
            var target = new FileHistoryTarget(candidate, environment.UserName, environment.MachineName);

            if (LongPath.DirectoryExists(target.DataDirectory))
            {
                return new FileHistoryLocation(FileHistoryLookup.Found, target);
            }
        }

        return new FileHistoryLocation(FileHistoryLookup.TargetNotFound);
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
        XmlFile.TryLoad(file) is { } document ? document.Descendants().SelectMany(ValuesOf) : [];

    /// <summary>
    /// A container element's own text is the concatenation of everything below it, which is never a
    /// path, so only a leaf contributes one.
    /// </summary>
    private static IEnumerable<string> ValuesOf(XElement element)
    {
        var attributes = element.Attributes().Select(attribute => attribute.Value);

        return element.HasElements ? attributes : attributes.Append(element.Value);
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
        // LongPath.Configured is the whole of the normalising: it refuses anything that is not
        // fully qualified, resolves relative segments, and drops a trailing separator. It handles
        // the device-namespace form a letterless drive is named in — which is what a File History
        // target frequently is — because LongPath.Display leaves that form alone (§6.3).
        if (LongPath.Configured(value) is not { } path)
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
}
