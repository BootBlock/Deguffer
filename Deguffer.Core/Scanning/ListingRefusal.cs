namespace Deguffer.Core.Scanning;

/// <summary>
/// Why Windows would not list a directory, for a listing that ended
/// <see cref="Safety.PathPresence.Refused"/>.
///
/// <para><b>Each answer asks something different of whoever reads it.</b> A folder the account may
/// not read stays refused however the listing is retried. A file at the path was not refused at all:
/// Windows listed nothing because there is no folder there to list, and a sentence that says Windows
/// would not let Deguffer look inside it blames a refusal that did not happen.</para>
/// </summary>
public enum ListingRefusal
{
    /// <summary>The listing was not refused: it was read to the end, or the directory is not there.</summary>
    None,

    /// <summary>The account is not allowed to read the directory, which no retry changes.</summary>
    Denied,

    /// <summary>Something other than a directory stands at the path, a file most often.</summary>
    NotADirectory,

    /// <summary>Any other failure: a sharing violation, a buffer a server rejects, a name too long.</summary>
    Other,
}
