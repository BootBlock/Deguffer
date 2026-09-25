using System.Text.RegularExpressions;
using System.Xml;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The catalogs and sessions Capture One's own settings name, and what became of reading them.
/// </summary>
/// <param name="Catalogs">Each catalog's own folder, the <c>.cocatalog</c> package, with no repeats.</param>
/// <param name="Sessions">Each session's folder, the one holding its <c>.cosessiondb</c>, with no repeats.</param>
/// <param name="SettingsFiles">Every settings file found, read or not.</param>
/// <param name="Unread">
/// The settings files that were there and not understood: refused, held, far larger than Capture One
/// writes, or not well-formed. Also every settings folder that would not be listed, since a file
/// inside it may be there. A catalog named only in one of these is unknown, so the lists above stop
/// being a claim about every catalog Capture One knows.
/// </param>
public sealed record CaptureOneDocumentList(
    IReadOnlyList<string> Catalogs,
    IReadOnlyList<string> Sessions,
    IReadOnlyList<string> SettingsFiles,
    IReadOnlyList<string> Unread)
{
    /// <summary>
    /// Whether Capture One may have kept settings for this user. A settings folder that would not be
    /// listed counts: it may hold settings, and "not used" is a claim nothing established.
    /// </summary>
    public bool Found => SettingsFiles.Count > 0 || Unread.Count > 0;

    /// <summary>Whether every settings file and folder found was read.</summary>
    public bool IsComplete => Unread.Count == 0;
}

/// <summary>
/// Finds Capture One's catalogs and sessions by reading the settings it keeps of the documents it
/// opened.
///
/// <para><b>A catalog or a session can be on any drive, and only Capture One's own record says
/// where.</b> A photographer keeps them on a shoot drive or an external disk as often as in the
/// profile, so no search of the profile would find them, and a search of every drive would find
/// folders that merely look like catalogs. Capture One keeps its settings as a .NET
/// <c>user.config</c> under <c>%LOCALAPPDATA%\Capture_One</c>, one per version, and under
/// <c>%LOCALAPPDATA%\Phase_One</c> for versions older than 21; its recently used documents are
/// listed in it.</para>
///
/// <para><b>The paths are taken by their extension, not by the setting that holds them.</b>
/// Capture One documents where the file is and names the recent-documents setting, but not the
/// shape of the value, and it was not installed where this was written. So every text and
/// attribute value in the file is read for full paths ending in <c>.cocatalog</c>,
/// <c>.cocatalogdb</c> or <c>.cosessiondb</c>, which is the one thing every shape of that value
/// must contain. A path found anywhere else in the file is no weaker: what makes a folder a catalog
/// is the database inside it, and the provider checks that on disk before anything is offered. A
/// session recorded by its folder alone, with no database file named, is not found.</para>
///
/// <para>The file also holds settings that are none of Deguffer's business, and may hold licence
/// details. Only the matched paths leave this type, and nothing read here is logged or shown.</para>
/// </summary>
public static partial class CaptureOneDocuments
{
    /// <summary>The name .NET gives a program's per-user settings file.</summary>
    public const string SettingsFileName = "user.config";

    /// <summary>
    /// The folders under <c>%LOCALAPPDATA%</c> that .NET named after Capture One's publisher: the
    /// current one, and the one versions before 21 used.
    /// </summary>
    public static readonly IReadOnlyList<string> CompanyFolders = ["Capture_One", "Phase_One"];

    /// <summary>
    /// How a settings folder for the program itself begins. .NET names it after the executable
    /// (<c>CaptureOne.exe_StrongName_…</c>), and a publisher's folder can hold other programs'.
    /// </summary>
    private const string ProgramFolderPrefix = "CaptureOne";

    /// <summary>
    /// Far past any settings file a desktop program writes. The recent-documents list is short, and
    /// the rest of the file is window positions and preferences.
    /// </summary>
    private const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Every catalog and session the Capture One settings under <paramref name="localAppData"/>
    /// name.
    /// </summary>
    public static CaptureOneDocumentList Read(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        var catalogs = new List<string>();
        var sessions = new List<string>();
        var files = new List<string>();
        var unread = new List<string>();

        foreach (var file in SettingsFilesUnder(localAppData, unread))
        {
            files.Add(file);

            if (PathsIn(file) is not { } paths)
            {
                unread.Add(file);
                continue;
            }

            foreach (var (path, extension) in paths)
            {
                switch (extension)
                {
                    case ".cocatalog":
                        AddOnce(catalogs, path);
                        break;

                    // The database file inside a catalog, named where a program records the file
                    // it opened rather than the package.
                    case ".cocatalogdb" when Path.GetDirectoryName(path) is { } catalog:
                        AddOnce(catalogs, catalog);
                        break;

                    // A session's folder must be inside a volume rather than the volume itself: a
                    // session file found at a drive's root would otherwise make the whole drive a
                    // session to walk (§5.2).
                    case ".cosessiondb" when Path.GetDirectoryName(path) is { } session
                        && Path.GetDirectoryName(session) is not null:
                        AddOnce(sessions, session);
                        break;
                }
            }
        }

        return new CaptureOneDocumentList(catalogs, sessions, files, unread);
    }

    /// <summary>
    /// Every <c>user.config</c> two levels below a Capture One settings folder: one folder per
    /// build of the program, and one per version inside it. A folder that would not be listed is
    /// recorded as unread, because a settings file inside it may name any catalog.
    /// </summary>
    private static IEnumerable<string> SettingsFilesUnder(string localAppData, List<string> unread)
    {
        foreach (var company in CompanyFolders)
        {
            var programs = ChildDirectories.Under(
                Path.Combine(localAppData, company),
                static name => name.StartsWith(ProgramFolderPrefix, StringComparison.OrdinalIgnoreCase));

            if (programs.Unreadable)
            {
                unread.Add(Path.Combine(localAppData, company));
                continue;
            }

            // A folder that is a link is read through, as the program reading its own settings
            // would. Only a file is read here, and nothing reached through it is ever a target.
            foreach (var program in programs.Directories.Concat(programs.Links))
            {
                var versions = ChildDirectories.Under(program.FullName);

                if (versions.Unreadable)
                {
                    unread.Add(LongPath.Display(program.FullName));
                    continue;
                }

                foreach (var version in versions.Directories.Concat(versions.Links))
                {
                    var file = LongPath.Display(Path.Combine(version.FullName, SettingsFileName));

                    // A file Windows would not describe is still a settings file found. Reading it
                    // fails, which is what reports it as unread.
                    if (LongPath.ProbeFile(file) is not PathPresence.Absent)
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every full path to a catalog or a session in <paramref name="file"/>, with its extension in
    /// lower case, or null where the file could not be read or is not well-formed XML.
    /// </summary>
    private static IReadOnlyList<(string Path, string Extension)>? PathsIn(string file)
    {
        if (BoundedFile.Read(file, MaximumBytes) is not { } content)
        {
            return null;
        }

        var found = new List<(string, string)>();

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        Collect(reader.Value, found);
                        break;

                    case XmlNodeType.Element when reader.HasAttributes:
                        while (reader.MoveToNextAttribute())
                        {
                            Collect(reader.Value, found);
                        }

                        break;
                }
            }
        }
        catch (XmlException)
        {
            // Not the file .NET writes. A part read before the fault is not the whole list, so none
            // of it is used, and the caller reports the file as unread.
            return null;
        }

        return found;
    }

    /// <summary>
    /// A DTD is refused rather than processed: an entity declared in a file another program wrote is
    /// not something Deguffer expands.
    /// </summary>
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static void Collect(string value, List<(string, string)> found)
    {
        foreach (Match match in DocumentPath().Matches(value))
        {
            // Configured refuses a relative path, which would otherwise resolve against Deguffer's
            // own working directory, and normalises the rest so one catalog written two ways is one.
            if (LongPath.Configured(match.Value) is { } path)
            {
                found.Add((path, "." + match.Groups["extension"].Value.ToLowerInvariant()));
            }
        }
    }

    private static void AddOnce(List<string> list, string path)
    {
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(path);
        }
    }

    /// <summary>
    /// A full path, on a drive or a share, that ends in one of Capture One's document extensions.
    /// The shortest such run is taken, so a value listing several paths yields each; the extension
    /// must not continue into a longer word, so <c>.cocatalogs</c> is no match. No colon may follow
    /// the drive, where Windows allows none, so a run cannot begin at an earlier path in the same
    /// value, or at a URI's scheme, and swallow the path that ends in the extension.
    /// </summary>
    [GeneratedRegex(
        @"(?:[A-Za-z]:[\\/]|\\\\)[^<>""|?*:\r\n\t]*?\.(?<extension>cocatalogdb|cocatalog|cosessiondb)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentPath();
}
