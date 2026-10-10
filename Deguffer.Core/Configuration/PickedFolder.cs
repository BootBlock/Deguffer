namespace Deguffer.Core.Configuration;

/// <summary>
/// Whether a folder the user chose in the system picker is one on a disk, and what they are told
/// when it is not.
///
/// <para>The picker's result is a string, and nothing in its contract promises a path: it lets a
/// person navigate to libraries, devices and namespace extensions, and the older picker returned an
/// empty path for some of them. A value that is not a fully qualified path is nothing Deguffer can
/// scan, approve or look in, and an exception about it from an <c>async void</c> handler ends the
/// process.</para>
///
/// <para>Here rather than behind each page for the reason <see cref="EnteredSetting"/> gives: three
/// of the four places that pick a folder widen what Deguffer looks at, and a check that exists
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
    /// <paramref name="picked"/> exactly as the picker returned it, or null where it is not a fully
    /// qualified path.
    ///
    /// <para>Not <see cref="Safety.LongPath.Configured"/>, which resolves the value through
    /// <see cref="Path.GetFullPath(string)"/>. That drops a trailing dot or space from the last
    /// segment, so a folder really named <c>build.</c> would come back as its sibling
    /// <c>build</c>, and Explore would scan a folder nobody picked. The stores that keep a picked
    /// folder normalise it themselves, through <see cref="Safety.LongPath.Entry"/>.</para>
    /// </summary>
    public static string? OnDisk(string picked) => Path.IsPathFullyQualified(picked) ? picked : null;
}
