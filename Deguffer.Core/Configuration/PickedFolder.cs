using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// What a folder the user chose in the system picker becomes, and what they are told when it is
/// not a folder on a disk.
///
/// <para>The picker lets a person navigate to places that are not folders on a disk at all: a
/// library, a phone or camera, a namespace extension. What comes back for one of those is an empty
/// path, or a shell name that is not a path. Neither is anything Deguffer can scan, approve or look
/// in, and before this rule the Explore page threw on the empty one from an <c>async void</c>
/// handler, which ends the process.</para>
///
/// <para>Here rather than behind each page for the reason <see cref="EnteredSetting"/> gives: two
/// of the three places that pick a folder widen what Deguffer looks at, and a check that exists
/// only inside a view-model is one nothing can hold Deguffer to.</para>
/// </summary>
public static class PickedFolder
{
    /// <summary>
    /// What the page says when the choice was not a folder on a disk. It names what to choose
    /// instead, because the reader has just done what the picker allowed and needs to know what
    /// Deguffer wanted.
    /// </summary>
    public const string NotOnDisk =
        "That is not a folder on a disk, so Deguffer cannot use it. Choose a folder on one of this "
        + "computer's drives or on a network share.";

    /// <summary>
    /// The folder <paramref name="picked"/> names, in the form <see cref="LongPath.Configured"/>
    /// leaves a configured path, or null where it names nothing on a disk.
    /// </summary>
    public static string? OnDisk(string picked) => LongPath.Configured(picked);
}
