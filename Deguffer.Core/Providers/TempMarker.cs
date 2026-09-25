using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// One entry a named tool writes into a folder it does not own, recognised by a name the tool's own
/// source gives it.
///
/// <para><b>§5.2 applied to a directory that belongs to nobody.</b> A temporary folder is not a tool
/// root, so nothing in it is attributable by where it sits: a 1.39 GB folder with a random name on
/// the machine that prompted this held Visual Studio's staged update, and a 7.27 GB one was a
/// running agent's live scratch. What makes an entry offerable is a name only one tool writes, and
/// an entry no marker recognises is left alone by every row built on these.</para>
/// </summary>
/// <param name="Tool">
/// What wrote the entry, as the row names it: the cause §2 promises rather than a path. It is also
/// the heading the entry is grouped under.
/// </param>
/// <param name="Name">
/// The entry's whole name, anchored at both ends and matched ignoring case, because NTFS does. As
/// narrow as the tool's own source allows: a name shape the source does not produce is not
/// recognised, which costs a smaller reclaim rather than somebody's folder.
/// </param>
/// <param name="Kind">
/// A directory removed whole, a directory emptied in place because the tool writes into it again, or
/// a file. An entry of the other kind with a matching name is not this marker's.
/// </param>
/// <param name="Reason">
/// Why the entry may go, or — for a marker that <see cref="Keeps"/> — why it must not.
/// </param>
public sealed record TempMarker(string Tool, Regex Name, TargetKind Kind, string Reason)
{
    /// <summary>
    /// Process names that, while any of them is running, hold back every entry this marker
    /// recognises.
    ///
    /// <para>A refusal rather than a warning, and coarser than it could be on purpose. Where a tool's
    /// scratch cannot be tied to the process using it — a name built from a random number, a thread
    /// identifier, or nothing at all — the only way to be sure a run is not using it is that the
    /// tool is not running. That costs an offer on a busy machine, where the other reading costs
    /// somebody the build they are waiting on.</para>
    /// </summary>
    public IReadOnlyList<string> HeldBy { get; init; } = [];

    /// <summary>
    /// Whether the tool says, entry by entry, that the entry is live. Null where it has no way to.
    /// Asked with the entry's name, because that is what the tools that answer this key on.
    /// </summary>
    public Func<string, bool>? InUse { get; init; }

    /// <summary>What a row says of an entry <see cref="InUse"/> held back.</summary>
    public string InUseReason { get; init; } = "The tool that wrote this is still using it, so it is left alone.";

    /// <summary>
    /// Whether this marker names something to leave alone rather than to take. Recognised all the
    /// same, so the row that knows what it is protects it and asserts it survived, and no other row
    /// takes it for being old.
    /// </summary>
    public bool Keeps { get; init; }

    /// <summary>Whether <paramref name="name"/>, an entry of <paramref name="isDirectory"/>'s kind, is this marker's.</summary>
    public bool Recognises(string name, bool isDirectory) =>
        isDirectory == (Kind is TargetKind.Directory or TargetKind.DirectoryContents) && Name.IsMatch(name);
}
