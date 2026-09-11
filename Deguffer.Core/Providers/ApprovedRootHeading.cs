using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The heading a directory found in the user's source folders is listed under: the approved folder it
/// was found in.
///
/// <para>Shared by every provider that searches approved folders, so a project reads under the same
/// heading on each of their rows. The nearest folder wins where one approved folder sits inside
/// another, because that is the folder the user approved with this project in mind.</para>
///
/// <para>Null where no approved folder holds the path. Discovery returns nothing outside one, so that
/// is not expected, and it costs only a heading: nothing about a run depends on it.</para>
/// </summary>
internal static class ApprovedRootHeading
{
    public static string? For(IReadOnlyList<string> approvedRoots, string path) =>
        approvedRoots
            .Where(root => LongPath.Contains(root, path))
            .MaxBy(root => root.Length);
}
