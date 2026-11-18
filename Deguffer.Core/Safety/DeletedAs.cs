namespace Deguffer.Core.Safety;

/// <summary>
/// What <see cref="EntryDeletion"/> was asked to delete, which decides how the other kind is refused,
/// whether a missing entry is an error, and which of the runtime's calls an entry goes to.
/// </summary>
internal enum DeletedAs
{
    /// <summary>A file, or a link to a file: <see cref="File.Delete"/>'s terms.</summary>
    File,

    /// <summary>An empty directory, or a link to a directory: <see cref="Directory.Delete(string)"/>'s terms.</summary>
    Directory,
}
