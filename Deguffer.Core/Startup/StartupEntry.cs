using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>
/// Which entry this is: the place it comes from and the name it has there. A <c>Run</c> value by its
/// name, a Startup folder item by its file name, and a packaged task by its package family and task
/// identifier. The <c>StartupApproved</c> value of the first two has the same name.
/// </summary>
/// <param name="Package">The package family, for a packaged task, and null for every other source.</param>
public sealed record StartupKey(StartupSource Source, string Name, string? Package = null)
{
    /// <summary>Registry and file names compare without case, so two keys do too.</summary>
    public bool Equals(StartupKey? other) =>
        other is not null
        && Source == other.Source
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Package, other.Package, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        Source,
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
        Package is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Package));
}

/// <summary>Whether an entry's program is there.</summary>
public enum ProgramStanding
{
    /// <summary>
    /// Nothing says: Windows would not answer, or the entry names its program in a way only Windows'
    /// search path resolves. The zero value, so a standing nobody set claims nothing.
    /// </summary>
    Unknown,

    Present,

    /// <summary>Windows says the file is not there.</summary>
    Missing,
}

/// <summary>One thing Windows starts at sign-in (§7.5), as it was read.</summary>
/// <param name="Title">
/// What the page calls it: the program's own description where it has one, as Settings does, and the
/// entry's name otherwise.
/// </param>
/// <param name="Command">
/// What the entry runs, as it records it: a <c>Run</c> value's command, a shortcut's target with its
/// arguments, or null for a packaged task, whose manifest names a program and no command.
/// </param>
/// <param name="Program">The program it starts as a full path, or null where it names none Deguffer can find.</param>
/// <param name="Publisher">Who made the program, from its version information or its package, or null.</param>
/// <param name="Approval">The <c>StartupApproved</c> value as read, or null for a packaged task, which has none.</param>
/// <param name="RunValue">The <c>Run</c> value as stored, for an entry from a <c>Run</c> key: §7.5's check compares it after a change.</param>
/// <param name="FileStamp">The Startup folder item's size and last write, for the same check.</param>
public sealed record StartupEntry(
    StartupKey Key,
    string Title,
    string? Command,
    string? Program,
    ProgramStanding Standing,
    string? Publisher,
    StartupState State,
    Approval? Approval,
    StoredValue? RunValue = null,
    FileStamp? FileStamp = null)
{
    /// <summary>When it was disabled, where Windows recorded a time.</summary>
    public DateTime? DisabledAt => Approval?.DisabledAt;

    /// <summary>Whether a change here goes through <c>StartupApproved</c> at all.</summary>
    public bool IsPackaged => Key.Source is StartupSource.PackagedTask;
}

/// <summary>Every entry one read found, and what it could not read.</summary>
/// <param name="Notes">
/// One sentence for each source that could not be read whole, so a short list says it is short rather
/// than reading as a machine that starts less.
/// </param>
public sealed record StartupReading(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Notes)
{
    public static StartupReading Empty { get; } = new([], []);

    /// <summary>The entry whose program is <paramref name="imagePath"/>, compared as Windows compares paths, or none.</summary>
    public IReadOnlyList<StartupEntry> Starting(string imagePath) =>
        [.. Entries.Where(e => e.Program is { } program && SamePath(program, imagePath))];

    /// <summary>
    /// Whether two full paths name one file. Windows compares paths without case, and the
    /// <c>\\?\</c> form names the same file as the plain one.
    /// </summary>
    public static bool SamePath(string one, string other) =>
        string.Equals(Plain(one), Plain(other), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>The entry <paramref name="key"/> names, or null where this reading has none.</summary>
    public StartupEntry? Find(StartupKey key) => Entries.FirstOrDefault(e => e.Key.Equals(key));
}

/// <summary>The words for a <see cref="ProgramStanding"/> taken from Windows' answer.</summary>
internal static class ProgramStandings
{
    public static ProgramStanding From(PathPresence? presence) => presence switch
    {
        PathPresence.Present => ProgramStanding.Present,
        PathPresence.Absent => ProgramStanding.Missing,
        _ => ProgramStanding.Unknown,
    };
}
