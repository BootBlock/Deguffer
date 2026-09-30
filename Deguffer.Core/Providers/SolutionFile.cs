using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The projects a Visual Studio solution names, read from a <c>.sln</c> or a <c>.slnx</c>.
///
/// <para>This is what makes an open solution evidence about a project in a subfolder of it. Visual
/// Studio works in the solution's own folder, which is above every project except one kept beside
/// the solution, so where it works says nothing about which projects it has open. The solution file
/// says exactly that. Nothing else about the folder does: a terminal at the root of a repository
/// sits in the same kind of place and is using none of the projects below it.</para>
/// </summary>
internal static partial class SolutionFile
{
    /// <summary>
    /// A ceiling on what is read, so that a file named like a solution is never held in memory
    /// whatever its size. One past it is answered as unreadable, which holds back every project below
    /// it rather than offering one.
    /// </summary>
    private const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>Whether <paramref name="fileName"/> is a solution, by the two names Visual Studio opens.</summary>
    public static bool IsSolution(string fileName) =>
        Path.GetExtension(fileName) is var extension
        && (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The folder of every project the solution at <paramref name="path"/> names, resolved against
    /// the solution's folder, or null where it could not be read or is not a solution at all.
    ///
    /// <para>Null rather than empty, because a caller has to tell "names none of them" from "could
    /// not tell". A solution that could not be read may name any project, so the first answer would
    /// offer the build output of a project an editor has open.</para>
    ///
    /// <para>Only entries that name a project file are kept. A <c>.sln</c> lists its solution folders
    /// in the same form as its projects, under the folder's name in place of a path, and a web site
    /// project under a URL. Neither is a folder a build writes into.</para>
    /// </summary>
    public static IReadOnlyList<string>? ProjectFolders(string path)
    {
        var entries = Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? FromXml(path)
            : FromText(path);

        if (entries is null)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(path)!;

        return
        [
            .. entries
                .Where(entry => Path.GetExtension(entry).EndsWith("proj", StringComparison.OrdinalIgnoreCase))

                // Either separator, because a .slnx is written with forward slashes and a .sln
                // with back slashes, and either may hold an absolute path or climb out with '..'.
                .Select(entry => LongPath.Configured(Path.Combine(folder, entry.Replace('/', Path.DirectorySeparatorChar))))
                .OfType<string>()
                .Select(project => Path.GetDirectoryName(project))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// The path of every <c>Project</c> line, or null where the file is not a solution. The header is
    /// asked for because a file in another encoding would otherwise read as a solution naming nothing.
    /// </summary>
    private static List<string>? FromText(string path)
    {
        if (BoundedFile.Read(path, MaximumBytes) is not { } content)
        {
            return null;
        }

        // One line ending, because the pattern anchors each line and a file written with bare
        // carriage returns would otherwise read as a solution naming nothing.
        var text = Encoding.UTF8.GetString(content.Span).ReplaceLineEndings("\n");

        if (!text.Contains("Microsoft Visual Studio Solution File", StringComparison.Ordinal))
        {
            return null;
        }

        return [.. ProjectLine().Matches(text).Select(match => match.Groups["path"].Value)];
    }

    /// <summary>The <c>Path</c> of every <c>Project</c> element, however deeply its folders nest.</summary>
    private static List<string>? FromXml(string path)
    {
        if (XmlFile.TryLoad(path) is not { Root.Name.LocalName: "Solution" } document)
        {
            return null;
        }

        return
        [
            .. document.Descendants()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => (string?)element.Attribute("Path"))
                .OfType<string>(),
        ];
    }

    [GeneratedRegex(
        @"^\s*Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectLine();
}
