namespace Deguffer.Core.Exploring.Links;

/// <summary>What one link points at, as the link records it.</summary>
/// <param name="Target">
/// The full path it leads to: a relative symbolic link already resolved against the folder it is in,
/// and a junction's object manager form put in the ordinary one.
/// </param>
public sealed record LinkTarget(LinkKind Kind, string Target);

/// <summary>
/// Reads where a link points, behind a seam so a broken link is proved against targets a test
/// describes rather than links the test machine has to be able to make and follow (G1).
/// </summary>
public interface ILinkTargetReader
{
    /// <summary>
    /// What <paramref name="path"/> points at, or null where it records no path: a shortcut to a shell
    /// folder, a file that will not load as a shortcut, a reparse point of a kind that is no link, or
    /// one Windows would not open. A null claims nothing about a target, and is never reported.
    /// </summary>
    /// <param name="isLink">
    /// Whether <paramref name="path"/> is a reparse point, as the listing that found it said. A shortcut
    /// is an ordinary file, read through the shell; a link is read from its reparse data.
    /// </param>
    LinkTarget? Read(string path, bool isLink);
}
