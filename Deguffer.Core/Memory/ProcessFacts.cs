namespace Deguffer.Core.Memory;

/// <summary>
/// Yes, no, or Windows would not say.
///
/// <para><b>Three values rather than two, because §7.2.1 refuses on the third exactly as it refuses
/// on the first.</b> A fact that read as <see cref="No"/> where Windows would not answer would let a
/// process through on a question nobody got an answer to.</para>
///
/// <para><see cref="Unreadable"/> is the zero value on purpose, so an answer nobody set reads as the
/// one that refuses.</para>
/// </summary>
public enum Answer
{
    /// <summary>Windows refused the question, or was not asked because an earlier answer made it moot.</summary>
    Unreadable,

    Yes,

    No,
}

/// <summary>Whether a process belongs to an application package, and whether Windows has frozen it.</summary>
public enum PackageAnswer
{
    /// <summary>Whether it belongs to a package could not be read.</summary>
    Unreadable,

    /// <summary>It has no package identity, so the question of a suspended package does not arise.</summary>
    NotPackaged,

    /// <summary>Packaged, and running.</summary>
    Running,

    /// <summary>
    /// Packaged, and frozen: Windows holds its pages only while nothing else needs them, so closing it
    /// buys nothing a user waited for (§7.2.1).
    /// </summary>
    Suspended,

    /// <summary>Packaged, and whether it is frozen could not be read.</summary>
    StateUnreadable,
}

/// <summary>One top-level window of a process, with the class Windows registered it under.</summary>
/// <param name="Handle">
/// The window, as Windows enumerated it. A window handle is recycled as a process identifier is, so
/// anything acting on one asks Windows again which process owns it at the moment it acts (§7.2.1).
/// </param>
public sealed record ProcessWindow(nint Handle, string ClassName);

/// <summary>
/// What Windows says about one process beyond its memory figures: its session, its account, its
/// integrity level, its criticality, its package state and its window set, read for the one row a
/// user picked, which is the list §7.2.1 names.
///
/// <para><b>The rest of §7.2.1's refusal table is not here, and is not meant to be.</b> Deguffer's own
/// process tree, the process owning the shell window, an image named <c>explorer.exe</c> or
/// <c>dwm.exe</c>, a service host, and a console host are all decided from the §7.2 snapshot the user
/// picked the row from, which already knows every process's identifier, parent and name. Opening a
/// process to ask any of them would be the thing §7.2 forbids.</para>
/// </summary>
/// <param name="Present">
/// Whether the process the caller named, by identifier and creation time, is the process that holds
/// that identifier now. <see cref="Answer.No"/> where it has exited or the identifier has passed to a
/// later process, and <see cref="Answer.Unreadable"/> where Windows would not open it or would not say
/// when it was created. Every other fact here is <see cref="Answer.Unreadable"/> unless this is
/// <see cref="Answer.Yes"/>: nothing was read, because there was nothing to read it from.
/// </param>
/// <param name="InOwnSession">Whether it runs in the session Deguffer runs in.</param>
/// <param name="AsOwnAccount">Whether it runs as the account Deguffer runs as.</param>
/// <param name="AboveOwnIntegrity">
/// Whether its integrity level is above Deguffer's own, which is what decides whether a posted message
/// would reach it at all. <see cref="IntegrityLevel"/> holds why the comparison is by value.
/// </param>
/// <param name="Critical">Whether Windows would stop the machine if the process ended.</param>
/// <param name="OwnsConsoleWindow">
/// Whether one of its top-level windows is a console's, whatever its state. A console's window is
/// reported against an <em>attached</em> process rather than against the console host, and closing a
/// console ends every process attached to it, so this refuses the whole process rather than one
/// window (§7.2.1).
/// </param>
/// <param name="Windows">
/// Its top-level windows that are unowned, visible and not cloaked, in the order Windows enumerated
/// them. <b>Empty where none qualifies, and null where the set could not be read</b>: a process whose
/// windows Windows would not describe is not a process with no windows.
/// </param>
public sealed record ProcessFacts(
    Answer Present,
    Answer InOwnSession,
    Answer AsOwnAccount,
    Answer AboveOwnIntegrity,
    Answer Critical,
    PackageAnswer Package,
    Answer OwnsConsoleWindow,
    IReadOnlyList<ProcessWindow>? Windows)
{
    /// <summary>
    /// Nothing was read, because <paramref name="present"/> says there was nothing to read it from.
    /// </summary>
    internal static ProcessFacts NothingRead(Answer present) => new(
        present,
        Answer.Unreadable,
        Answer.Unreadable,
        Answer.Unreadable,
        Answer.Unreadable,
        PackageAnswer.Unreadable,
        Answer.Unreadable,
        Windows: null);
}

/// <summary>
/// Asks Windows about <b>one</b> process, the one a user picked, at the moment of asking.
///
/// <para><b>One process, because §7.2 forbids asking for the rest.</b> A picture of five hundred
/// processes redrawn every two seconds must not open five hundred processes to decide what it would
/// refuse, and a verdict nobody asked for is a verdict nobody reads (§7.2.1).</para>
///
/// <para>The seam between what Windows says and what <c>MemoryActionPolicy</c> decides from it, so
/// those decisions are tested against facts a test wrote rather than against this machine's.</para>
/// </summary>
public interface IProcessFactSource
{
    /// <summary>
    /// What Windows says about the process created at <paramref name="creationTime"/> and holding
    /// <paramref name="processId"/> now.
    /// </summary>
    /// <param name="processId">Its identifier. It must be positive: the idle process at 0 answers as a free identifier does.</param>
    /// <param name="creationTime">
    /// When the snapshot the user picked from says it was created, as a FILETIME in UTC and exact to
    /// the tick. An identifier alone is not an identity, because Windows reuses identifiers.
    /// </param>
    ProcessFacts Read(int processId, long creationTime, CancellationToken ct);
}
