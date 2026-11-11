namespace Deguffer.Core.Exploring.Links;

/// <summary>What kind of link points somewhere else.</summary>
public enum LinkKind
{
    /// <summary>A <c>.lnk</c> file, which the shell reads and the file system does not.</summary>
    Shortcut,

    /// <summary>A symbolic link to a file or a folder, which the file system follows.</summary>
    SymbolicLink,

    /// <summary>
    /// A junction, which the file system follows to a folder. A folder a volume is mounted at is one
    /// too, and points at the volume rather than at a path.
    /// </summary>
    Junction,
}
