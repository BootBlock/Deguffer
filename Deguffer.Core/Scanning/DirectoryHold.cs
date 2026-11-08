namespace Deguffer.Core.Scanning;

/// <summary>Whether a walk keeps each directory it lists open while its callback reads the contents.</summary>
internal enum DirectoryHold
{
    /// <summary>The directory is closed as soon as it is listed.</summary>
    None,

    /// <summary>
    /// The directory stays open until the callback returns or throws, and
    /// <see cref="DirectoryContents.Handle"/> gives its handle, so the callback can read each file
    /// relative to it. A walk that removes entries has no use for it, and an open handle can delay
    /// the removal of the directory it is open on.
    /// </summary>
    DuringCallback,
}
