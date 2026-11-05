namespace Deguffer.Core.Startup;

/// <summary>
/// What the Startup page hands to Windows for one entry, a file or text, or the sentence that says why
/// there is none. Exactly one of the two is set, which only the two factories can build.
/// </summary>
public sealed record StartupHandoff
{
    private StartupHandoff(string? value, string? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    /// <summary>The file as a full path, or the text, and null where there is none to hand over.</summary>
    public string? Value { get; }

    /// <summary>Why there is none, for the reader, and null where <see cref="Value"/> is set.</summary>
    public string? Refusal { get; }

    public static StartupHandoff Of(string value) => new(value, null);

    public static StartupHandoff Refused(string reason) => new(null, reason);
}

/// <summary>
/// The files behind one entry that the Startup page's row menu shows in File Explorer, and the text it
/// copies (§7.5): the program the entry starts, and for a Startup folder entry the item in the folder.
///
/// <para>None of this changes anything, which is why none of it is behind
/// <see cref="StartupChangePolicy"/>. A file that cannot be shown is still a sentence rather than a
/// disabled menu item, for the reason §7.5 gives a refused change one.</para>
/// </summary>
public static class StartupFiles
{
    /// <summary>
    /// The program <paramref name="entry"/> starts. Refused where Deguffer cannot name it, and where
    /// Windows says it is not there: Explorer asked to select a file that is not there opens a folder
    /// nobody asked for instead of saying so.
    /// </summary>
    public static StartupHandoff Program(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry switch
        {
            { Program: null } => StartupHandoff.Refused(
                "Deguffer cannot tell which program this entry starts, so there is no file to show."),
            { Standing: ProgramStanding.Missing } => StartupHandoff.Refused(
                $"Windows says {entry.Program} is not there, so there is no file to show."),
            _ => StartupHandoff.Of(entry.Program),
        };
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is an item in a Startup folder, the only kind with an item to
    /// show there. The menu offers <see cref="Item"/> only where this holds.
    /// </summary>
    public static bool HasItem(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return StartupSources.Folders.Contains(entry.Key.Source);
    }

    /// <summary>
    /// The item <paramref name="entry"/> is in its Startup folder, or null for an entry from any other
    /// source, which has no item to show. <paramref name="folder"/> is where that folder is, as
    /// <see cref="StartupFolderReader.FolderOf"/> answers, and null where Windows would not say.
    /// </summary>
    public static StartupHandoff? Item(StartupEntry entry, string? folder)
    {
        if (!HasItem(entry))
        {
            return null;
        }

        return folder is null
            ? StartupHandoff.Refused("Windows would not say where this Startup folder is, so there is no file to show.")
            : StartupHandoff.Of(Path.Combine(folder, entry.Key.Name));
    }

    /// <summary>
    /// What the entry runs, as it records it, for the clipboard: the command where it has one, and the
    /// program a packaged task names otherwise.
    /// </summary>
    public static StartupHandoff Command(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return (entry.Command ?? entry.Program) is { } text
            ? StartupHandoff.Of(text)
            : StartupHandoff.Refused("This entry does not name what it runs in a way Deguffer can read, so there is nothing to copy.");
    }
}
