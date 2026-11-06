using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What a listing keeps of the files it meets. A directory is always kept with its name, because the
/// walk enters it by that name.
///
/// <para><b>Why a listing is told.</b> A name is a string for every file, and most walks never read
/// one: a walk that totals sizes reads a file's length and timestamps, and a search for folders does
/// not look at files at all. Across a tree of millions of files, each of those strings is built and
/// thrown away. A walk that keeps a file names it, or lists it without a name; one that keeps none
/// leaves files out of the listing.</para>
///
/// <para><b>§9 is the one name every kept file is asked for.</b> Every choice here that keeps files
/// names each one that is an Outlook data file, so a file listed without a name is one that is not a
/// store, and <see cref="WalkEntry.IsMailStore"/> can answer without the name. A walk cannot choose
/// to leave stores unnamed.</para>
/// </summary>
public readonly struct FileNames
{
    private readonly Keeping _keeping;
    private readonly string? _alsoNamed;
    private readonly bool _lends;

    private FileNames(Keeping keeping, string? alsoNamed, bool lends = false)
    {
        _keeping = keeping;
        _alsoNamed = alsoNamed;
        _lends = lends;
    }

    /// <summary>Every file, each with its name. Also what a default value means.</summary>
    public static FileNames All => new(Keeping.All, null);

    /// <summary>No file at all: the listing holds directories and links alone.</summary>
    public static FileNames None => new(Keeping.None, null);

    /// <summary>Every file, named only where it is an Outlook data file.</summary>
    public static FileNames Stores => new(Keeping.Stores, null);

    /// <summary>
    /// Every file, named where it is an Outlook data file or is called <paramref name="name"/>,
    /// compared ignoring case as Windows compares names.
    /// </summary>
    public static FileNames StoresAnd(string name) => new(Keeping.Stores, name);

    /// <summary>
    /// Every file, and every name, files' and folders' alike, lent from the listing worker's memory
    /// rather than built as a string (<see cref="ListedName"/>). Each can be read only while the caller
    /// holds the directory's contents, so this is for a walk that copies every name elsewhere at
    /// once, as the Explore walk copies each into its tree's name pool.
    /// </summary>
    public static FileNames AllLent => new(Keeping.All, null, lends: true);

    /// <summary>Whether files are listed at all.</summary>
    public bool KeepsFiles => _keeping is not Keeping.None;

    /// <summary>Whether the names kept are lent from the listing's memory (<see cref="AllLent"/>).</summary>
    internal bool Lends => _lends;

    /// <summary>Whether a kept file called <paramref name="name"/> keeps its name.</summary>
    public bool Names(ReadOnlySpan<char> name) =>
        _keeping is Keeping.All
        || MailStore.Is(name)
        || (_alsoNamed is not null && name.Equals(_alsoNamed, StringComparison.OrdinalIgnoreCase));

    // All first, so a default value loses nothing.
    private enum Keeping
    {
        All,
        Stores,
        None,
    }
}
