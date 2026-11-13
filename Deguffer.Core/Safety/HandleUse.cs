namespace Deguffer.Core.Safety;

/// <summary>What a handle opened on a path is for, which decides whether a link on it is followed.</summary>
internal enum HandleUse
{
    /// <summary>Learning where a path really is, so every link on the way, its own name included, is followed.</summary>
    Resolve,

    /// <summary>Describing the entry the path names: a link is described as the link, never its target.</summary>
    Describe,
}
