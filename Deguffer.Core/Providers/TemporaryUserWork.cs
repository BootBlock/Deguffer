using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>An entry of a temporary folder that holds somebody's work.</summary>
/// <param name="Path">The entry, in display form.</param>
/// <param name="What">What it is, as a phrase that completes "it is".</param>
internal sealed record UserWorkEntry(string Path, string What);

/// <summary>What one temporary folder was found to hold of somebody's work.</summary>
/// <param name="Entries">Each entry recognised as somebody's work, a link among them included.</param>
/// <param name="Unreadable">
/// Whether Windows would not list the folder, so nothing here says it holds none.
/// </param>
internal sealed record UserWorkScan(IReadOnlyList<UserWorkEntry> Entries, bool Unreadable);

/// <summary>
/// Work a program keeps in a temporary folder until the user saves it somewhere else, recognised by
/// name, one entry of a temporary folder at a time, folders and files alike.
///
/// <para><b>Tier 3 content in a Tier 2 folder.</b> The temporary files row offers what a program left
/// behind and forgot, and tells it apart only by age and by whether something is using it. Neither
/// protects an entry that is the only copy of somebody's work: it may be a week old, and the program
/// that made it may be closed. So an entry recognised here is never offered, whatever its age, and
/// §5.6 asserts after every clean that it survived.</para>
///
/// <para><b>Not offered on any row.</b> Deguffer cannot tell an abandoned experiment from a
/// week-old piece of work, and losing the second is permanent. No other row recognises these names,
/// so the temporary files row is the only one that could take them, and it is the one that keeps
/// them.</para>
/// </summary>
internal static partial class TemporaryUserWork
{
    /// <summary>
    /// Each kind of entry, by its name and by whether it is a folder.
    ///
    /// <list type="bullet">
    /// <item>Arduino IDE 2 keeps every sketch made with <i>File &gt; New</i> in a folder whose name is
    /// <c>.arduinoIDE-unsaved</c> followed by what the <c>temp</c> package adds, such as
    /// <c>.arduinoIDE-unsaved2022615-21100-iahybb.yyvh</c> (<c>TempSketchPrefix</c> in the IDE's
    /// <c>is-temp-sketch.ts</c>). The folder survives the IDE closing, and the IDE offers the sketch
    /// again at its next start.</item>
    /// <item>Krita autosaves a document that was never saved into the temporary folder on Windows
    /// (<c>KisAutoSaveRecoveryDialog::autoSaveLocation</c>), as
    /// <c>krita-&lt;process&gt;-&lt;document&gt;-autosave.kra</c>, with a leading <c>.</c> where its
    /// setting hides unnamed autosaves (<c>KisDocument::generateAutoSaveFileName</c>). The patterns are
    /// the two <c>KisApplication::checkAutosaveFiles</c> offers back for recovery at start-up. Krita
    /// deletes the file only when the document is closed normally, so a crash leaves it. A saved
    /// document's autosave goes beside the document, and its <c>.kra~</c> backups are copies of a
    /// saved file, so neither is here.</item>
    /// <item>Blender writes <c>quit.blend</c> when it exits, which <i>File › Recover › Last
    /// Session</i> opens, and autosaves as <c>&lt;process&gt;_autosave.blend</c>, or
    /// <c>&lt;file&gt;_&lt;process&gt;_autosave.blend</c> once the file has a name, which
    /// <i>File › Recover › Auto Save</i> opens.</item>
    /// <item>NVIDIA Highlights, in GeForce Experience and the NVIDIA app, captures every clip into
    /// <c>Highlights\&lt;game&gt;</c> under the temporary folder, and the player saves the ones they
    /// want to the Gallery from there (the GfeSDK developer and Highlights FAQs). Until then the clip
    /// is the only copy, and NVIDIA bounds the folder itself by deleting the oldest clips first under
    /// a limit the player sets. A location the player moved is not followed: where NVIDIA records it
    /// is not known.</item>
    /// </list>
    ///
    /// <para>Compared without regard to case, as NTFS compares names.</para>
    /// </summary>
    private static readonly Kind[] Kinds =
    [
        new(ArduinoSketch(), IsFolder: true, "an Arduino IDE sketch that was never saved"),
        new(KritaAutosave(), IsFolder: false, "Krita's autosave of an image that was never saved"),
        new(BlenderQuit(), IsFolder: false, "Blender's copy of its last session, which File › Recover › Last Session opens"),
        new(BlenderAutosave(), IsFolder: false, "a file Blender autosaved, which File › Recover › Auto Save opens"),
        new(NvidiaHighlights(), IsFolder: true, "the folder of NVIDIA Highlights clips, which may not have been saved to the Gallery"),
    ];

    [GeneratedRegex(@"\A\.arduinoIDE-unsaved", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArduinoSketch();

    [GeneratedRegex(@"\A\.?krita-.*-.*-autosave\.kra\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KritaAutosave();

    [GeneratedRegex(@"\Aquit\.blend\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlenderQuit();

    [GeneratedRegex(@"\A(?:.+_)?[0-9]+_autosave\.blend\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlenderAutosave();

    [GeneratedRegex(@"\AHighlights\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaHighlights();

    /// <summary>The entries directly inside <paramref name="folder"/> that hold somebody's work.</summary>
    public static UserWorkScan In(string folder)
    {
        var entries = FolderEntries.Of(
            folder,
            static (ref FileSystemEntry entry) => new UserWorkEntry(
                LongPath.Display(entry.ToFullPath()),
                Recognise(entry.FileName, entry.IsDirectory)!),
            static (ref FileSystemEntry entry) => Recognise(entry.FileName, entry.IsDirectory) is not null);

        return new UserWorkScan(entries ?? [], Unreadable: entries is null);
    }

    /// <summary>
    /// Why a temporary folder that could not be searched for unsaved work is left alone whole, in the
    /// plan and in Explore.
    /// </summary>
    public const string UnlistedReason =
        "Deguffer could not look in this temporary folder for work that was never saved, so it leaves "
        + "all of it alone.";

    /// <summary>Why an entry holding somebody's work survives, in the plan and in Explore.</summary>
    public static string Reason(UserWorkEntry entry) =>
        $"This is {entry.What}. It may be the only copy of that work, so Deguffer never removes it, "
        + "however old it is.";

    /// <summary>
    /// The sentence saying what the temporary files row keeps because it is somebody's work, or null
    /// where it keeps none. Information, because nothing is lost: the reader learns why the folder is
    /// not emptied.
    /// </summary>
    public static PlanNote? Note(IReadOnlyList<UserWorkEntry> entries) => entries.Count == 0
        ? null
        : new PlanNote(
            PlanNoteSeverity.Information,
            (entries.Count == 1
                ? "One entry in these folders holds work that was never saved anywhere else"
                : $"{entries.Count} entries in these folders hold work that was never saved anywhere else")
            + (entries.Count == 1
                ? ", so this row leaves it alone however old it is: "
                : ", so this row leaves them alone however old they are: ")
            + $"{string.Join("; ", entries.Select(e => e.What).Distinct(StringComparer.Ordinal))}.");

    /// <summary>
    /// The warning for temporary folders that could not be searched for unsaved work, or null where
    /// every one was.
    /// </summary>
    public static PlanNote? UnlistedNote(int folders) => folders == 0
        ? null
        : new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not look for work that was never saved in {folders} temporary folder(s), "
            + "so it offers nothing in them.");

    /// <summary>
    /// What an entry named <paramref name="name"/> is, or null where it is nobody's work. A link is
    /// read as the kind of entry it presents as, and is kept on the same terms: it is never followed,
    /// and removing it is still taking the work away from where its program looks.
    /// </summary>
    private static string? Recognise(ReadOnlySpan<char> name, bool isFolder)
    {
        foreach (var kind in Kinds)
        {
            if (kind.IsFolder == isFolder && kind.Name.IsMatch(name))
            {
                return kind.What;
            }
        }

        return null;
    }

    /// <param name="Name">The entry's whole name, or the start of it where the program adds to it.</param>
    /// <param name="IsFolder">Whether the program writes a folder rather than a file.</param>
    /// <param name="What">What the entry is, as a phrase that completes "it is".</param>
    private sealed record Kind(Regex Name, bool IsFolder, string What);
}
