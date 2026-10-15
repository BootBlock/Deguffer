using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What a listing keeps of the files it meets. A directory is always kept with its name, because the
/// walk enters it by that name.
///
/// <para><b>Why a listing is told.</b> A name is a string for every file, and most walks never read
/// one: a walk that totals sizes reads a file's length and timestamps, and a search for folders does
/// not look at files at all. Across a tree of millions of files the names were most of what the walk
/// allocated. A walk that keeps a file names it, or lists it without a name; one that keeps none
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

    private FileNames(Keeping keeping, string? alsoNamed)
    {
        _keeping = keeping;
        _alsoNamed = alsoNamed;
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

    /// <summary>Whether files are listed at all.</summary>
    public bool KeepsFiles => _keeping is not Keeping.None;

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
