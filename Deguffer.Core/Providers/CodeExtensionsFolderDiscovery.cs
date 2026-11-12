using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One Code - OSS editor's extensions folder, identified by the list the editor keeps in it.</summary>
/// <param name="Path">The folder, in display form.</param>
/// <param name="Source">
/// What found it, as the user would recognise it: the profile folder's own name (<c>.vscode</c>,
/// <c>.vscode-insiders</c>, <c>.cursor</c>), which each editor takes from its <c>product.json</c>, or
/// the variable that moved it.
/// </param>
/// <param name="Listed">
/// The last name of every location the editor's <c>extensions.json</c> lists, compared ignoring case.
/// A folder named here is an installed extension, whatever its name looks like.
/// </param>
internal sealed record CodeExtensionsFolder(string Path, string Source, IReadOnlySet<string> Listed);

/// <summary>What a search found, and what it met on the way and did not look past.</summary>
/// <param name="Declined">Every identified folder reached through a link, and every path Windows would not describe, for §5.6.</param>
/// <param name="Unreadable">Whether a folder on the way would not be listed or described.</param>
/// <param name="DeclinedVariableFolder">
/// The folder the variable names where it is declined, such as a drive root, or null. The editor still
/// installs into it, so the provider declares it with <see cref="ToolRoot.Sparing"/>.
/// </param>
internal sealed record CodeExtensionsSearch(
    IReadOnlyList<CodeExtensionsFolder> Folders,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable,
    string? DeclinedVariableFolder);

/// <summary>
/// Finds the folders Code - OSS editors install extensions into: <c>&lt;profile&gt;\&lt;dot-folder&gt;\extensions</c>
/// for every editor, and the folder <see cref="Variable"/> names.
///
/// <para><b>Read from the editor's own resolution, never guessed.</b> The editor takes
/// <c>--extensions-dir</c>, then <see cref="Variable"/>, then <c>VSCODE_PORTABLE\extensions</c>, then
/// <c>&lt;home&gt;\&lt;dataFolderName&gt;\extensions</c>, where <c>dataFolderName</c> comes from each
/// product's <c>product.json</c> (<c>.vscode</c>, <c>.vscode-oss</c>, <c>.cursor</c>). A command-line
/// option and a portable install cannot be seen from outside the editor, so those two are not
/// reached, and the row says so.</para>
///
/// <para><b>Identified by the editor's own list, never by the folder's name.</b> A folder qualifies
/// only where it holds an <see cref="ListFile"/> that is a JSON array of entries each carrying an
/// <c>identifier</c> with a string <c>id</c> and a <c>location</c>, which is the shape the editor's
/// profile scanner accepts. Every dot-folder in a profile is asked, so the editor's name never needs
/// to be known, and a folder some other tool calls <c>extensions</c> is not entered. An empty list is
/// not enough: it says nothing about who wrote it, and reclaiming nothing is the safe direction to be
/// wrong in.</para>
///
/// <para>Separate from the provider because it answers "which folders are an editor's?", and the
/// provider answers "what inside one may go?".</para>
/// </summary>
internal static class CodeExtensionsFolderDiscovery
{
    /// <summary>The variable that moves every editor's extensions folder, as the editor reads it.</summary>
    public const string Variable = "VSCODE_EXTENSIONS";

    /// <summary>The folder's name inside an editor's profile folder.</summary>
    public const string FolderName = "extensions";

    /// <summary>The editor's list of installed extensions, which identifies the folder.</summary>
    public const string ListFile = "extensions.json";

    /// <summary>
    /// The most of <see cref="ListFile"/> read. About a kilobyte an extension is what the editor
    /// writes, so this is several thousand extensions, and a larger file is not the list.
    /// </summary>
    private const int MaximumListBytes = 8 * 1024 * 1024;

    /// <summary>Every extensions folder found, each identified, with what the search met on the way.</summary>
    /// <param name="machine">The machine's own folders, which a folder the variable names must not hold.</param>
    /// <param name="volumes">Asked every other path a folder the variable names is reachable at.</param>
    public static CodeExtensionsSearch Search(
        IUserEnvironment environment,
        ISystemDirectories machine,
        IVolumeInventory volumes,
        CancellationToken ct)
    {
        var folders = new List<CodeExtensionsFolder>();
        var declined = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var unreadable = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Unreached(string path)
        {
            notes.Add(UnreadableRoot.UnreachedNote(path));
            declined.Add((path, UnreadableRoot.UnreachedReason));
            unreadable = true;
        }

        // Whether the folder is an editor's, adding it where it is. A link is identified through, which
        // only reads, so the reader is told the folder exists, and is never classified or entered.
        void Consider(string folder, string source)
        {
            switch (LongPath.ProbeDirectory(folder, out var isLink))
            {
                case PathPresence.Absent:
                    return;

                case PathPresence.Refused:
                    Unreached(folder);
                    return;
            }

            if (Listed(folder) is not { } listed)
            {
                return;
            }

            if (isLink is true)
            {
                notes.Add(CacheLevelWalk.Note(folder));
                declined.Add((folder, CacheLevelWalk.LinkReason));
                return;
            }

            if (seen.Add(LongPath.Unaliased(Path.TrimEndingDirectorySeparator(folder))))
            {
                folders.Add(new CodeExtensionsFolder(folder, source, listed));
            }
        }

        var profile = ChildDirectories.Under(environment.UserProfile, static name => name is ['.', ..]);

        if (profile.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(environment.UserProfile));
            unreadable = true;
        }

        // A dot-folder that is a link is neither followed nor named, on VsCodeUserDataDiscovery's
        // reasoning: this walk chooses which folders to look in, and what such a link points at was never
        // identified as anything.
        foreach (var dotFolder in profile.Directories)
        {
            ct.ThrowIfCancellationRequested();

            Consider(Path.Combine(LongPath.Display(dotFolder.FullName), FolderName), dotFolder.Name);
        }

        // No default folder: where the variable is unset, the dot-folders above are the whole answer, so
        // the setting is read only where it has a value.
        var setting = ConfiguredFolder.FromVariable(Variable, defaultFolder: string.Empty, environment, machine, volumes);

        if (setting is { Declined: { } why, Value: { } value })
        {
            notes.Add(Declined(setting.Named ?? value, why));
        }
        else if (setting is { Value: not null, Folder: { } named })
        {
            Consider(named, Variable);
        }

        return new CodeExtensionsSearch(folders, declined, notes, unreadable, setting.DeclinedFolder);
    }

    /// <summary>
    /// The last name of every location <paramref name="folder"/>'s list names, or null where the folder
    /// holds no list in the editor's shape.
    /// </summary>
    internal static IReadOnlySet<string>? Listed(string folder)
    {
        using var document = BoundedJsonFile.ReadArray(Path.Combine(folder, ListFile), MaximumListBytes);

        if (document is null || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("identifier", out var identifier)
                || identifier.ValueKind != JsonValueKind.Object
                || BoundedJsonFile.StringProperty(identifier, "id") is null
                || !entry.TryGetProperty("location", out var location))
            {
                return null;
            }

            // Every form the editor writes a location in, because it resolves whichever it finds first and
            // an older editor wrote only some of them. Each is reduced to its last name, so a folder the
            // list names is kept wherever the list thinks the folder is.
            AddLastName(names, BoundedJsonFile.StringProperty(entry, "relativeLocation"));

            switch (location.ValueKind)
            {
                case JsonValueKind.String:
                    AddLastName(names, location.GetString());
                    break;

                case JsonValueKind.Object:
                    AddLastName(names, BoundedJsonFile.StringProperty(location, "fsPath"));
                    AddLastName(names, BoundedJsonFile.StringProperty(location, "path"));
                    break;

                default:
                    return null;
            }
        }

        return names;
    }

    private static void AddLastName(HashSet<string> names, string? location)
    {
        if (location?.TrimEnd('/', '\\') is not { Length: > 0 } trimmed)
        {
            return;
        }

        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];

        if (name.Length > 0)
        {
            names.Add(name);
        }
    }

    private static PlanNote Declined(string folder, string why) => new(
        PlanNoteSeverity.Warning,
        $"{Variable} names '{folder}' as the extensions folder, and Deguffer is not looking there: {why}");
}
