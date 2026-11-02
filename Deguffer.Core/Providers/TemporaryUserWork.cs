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
/// name, one child of a temporary folder at a time.
///
/// <para><b>Tier 3 content in a Tier 2 folder.</b> The temporary files row offers what a program left
/// behind and forgot, and tells it apart only by age and by whether something is using it. Neither
/// protects an entry that is the only copy of somebody's work: it may be a week old, and the program
/// that made it may be closed. So an entry recognised here is never offered, whatever its age, and
/// §5.6 asserts after every clean that it survived.</para>
///
/// <para><b>Not offered on any row.</b> Deguffer cannot tell an abandoned experiment from a
/// week-old piece of work, and losing the second is permanent.</para>
/// </summary>
internal static class TemporaryUserWork
{
    /// <summary>
    /// Each kind of entry by the start of its name, which its program writes in one case.
    ///
    /// <para>Arduino IDE 2 keeps every sketch made with <i>File &gt; New</i> in a folder whose name is
    /// <c>.arduinoIDE-unsaved</c> followed by what the <c>temp</c> package adds, such as
    /// <c>.arduinoIDE-unsaved2022615-21100-iahybb.yyvh</c> (<c>TempSketchPrefix</c> in the IDE's
    /// <c>is-temp-sketch.ts</c>). The folder survives the IDE closing, and the IDE offers the sketch
    /// again at its next start.</para>
    ///
    /// <para>Compared without regard to case, because a looser match keeps more and a keep is the
    /// safe direction to be wrong in.</para>
    /// </summary>
    private static readonly (string Prefix, string What)[] Kinds =
    [
        (".arduinoIDE-unsaved", "an Arduino IDE sketch that was never saved"),
    ];

    /// <summary>The entries directly inside <paramref name="folder"/> that hold somebody's work.</summary>
    public static UserWorkScan In(string folder)
    {
        var children = ChildDirectories.Under(folder, static name => Recognise(name) is not null);

        return new UserWorkScan(
        [
            .. children.Directories.Concat(children.Links).Select(directory => new UserWorkEntry(
                LongPath.Display(directory.FullName),
                Recognise(directory.Name)!)),
        ],
        children.Unreadable);
    }

    /// <summary>What an entry named <paramref name="name"/> is, or null where it is nobody's work.</summary>
    private static string? Recognise(ReadOnlySpan<char> name)
    {
        foreach (var (prefix, what) in Kinds)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return what;
            }
        }

        return null;
    }
}
