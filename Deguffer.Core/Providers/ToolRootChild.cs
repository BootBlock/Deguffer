namespace Deguffer.Core.Providers;

/// <summary>What an entry directly inside a <see cref="ToolRoot"/> is on disk.</summary>
public enum ChildKind
{
    /// <summary>A directory that is not a link.</summary>
    Folder,

    /// <summary>A file that is not a link.</summary>
    File,

    /// <summary>
    /// A junction or symbolic link, whichever kind of entry it points at. A plan never classifies
    /// what is behind one, so a link that carries a recognised name is still not what the provider
    /// recognised.
    /// </summary>
    Link,
}

/// <summary>
/// A child of a <see cref="ToolRoot"/> as its <see cref="ToolRoot.Recognises"/> predicate is asked
/// about it: the name the provider classifies, and what the entry with that name is.
/// </summary>
public readonly record struct ToolRootChild(string Name, ChildKind Kind);
