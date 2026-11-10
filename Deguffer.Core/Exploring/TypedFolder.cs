using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What a location typed into Explore names, and what the page says when it names no folder.
///
/// <para>A share is the place a person types rather than browses to: the system picker lists only
/// the servers it has found, and a share on a server it has not is reached by its name. So the path
/// typed is checked as a picked one is not: <see cref="PickedFolder"/> trusts the shell to have
/// confirmed the folder, and nothing confirmed this one.</para>
///
/// <para>Two halves, because the second asks the machine. <see cref="Folder"/> reads the text, and
/// <see cref="Refusal"/> words the answer of a probe the caller runs off the UI thread: on a share,
/// asking whether a folder is there waits on the server, for as long as the redirector waits for
/// one that does not answer.</para>
/// </summary>
public static class TypedFolder
{
    /// <summary>
    /// What the page says when the text is not a full path. It shows both forms, because the reader
    /// has typed something and needs to see what Deguffer wanted instead.
    /// </summary>
    public const string NotAPath =
        @"That is not a full path, so Deguffer cannot scan it. Type a share as \\server\share, or a "
        + @"folder as D:\Folder.";

    /// <summary>
    /// The folder <paramref name="typed"/> names, in display form, or null where it is not a full
    /// path to one.
    ///
    /// <para>Space around it, and one pair of quotes, come off: a path copied from File Explorer's
    /// "Copy as path" arrives quoted. Nothing else is changed, for the reason
    /// <see cref="PickedFolder.OnDisk"/> gives: resolving the path would drop a trailing dot or
    /// space from its last name and scan a folder nobody named.</para>
    ///
    /// <para>The extended-length form is read as the path it spells (§6.3), and any other device
    /// path is refused. <c>\\.\PhysicalDrive0</c> is fully qualified, and is a disk rather than a
    /// folder on one.</para>
    /// </summary>
    public static string? Folder(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var text = typed.Trim();

        // Not trimmed again: inside the quotes, a trailing space is part of the last name.
        if (text is ['"', .., '"'])
        {
            text = text[1..^1];
        }

        if (text.Length == 0)
        {
            return null;
        }

        var shown = LongPath.Display(text);

        return LongPath.IsDeviceSpelling(shown) ? null : PickedFolder.OnDisk(shown);
    }

    /// <summary>
    /// Why Explore will not point at <paramref name="folder"/>, given what a probe of it answered,
    /// or null where it is a folder.
    ///
    /// <para>A refusal is not an absence (<see cref="PathPresence.Refused"/>), so the two are told
    /// apart: a share this account may not open, and a server that did not answer, are both there
    /// to be fixed, where a name typed wrong is not.</para>
    /// </summary>
    public static string? Refusal(string folder, PathPresence presence) =>
        presence switch
        {
            PathPresence.Present => null,
            PathPresence.Absent =>
                $"There is no folder at '{folder}'. Check the name, and that the computer sharing it is "
                + "switched on and connected.",
            _ =>
                $"Windows would not say whether '{folder}' is a folder. A share this account may not open, "
                + "and a computer that did not answer, both do that.",
        };
}
