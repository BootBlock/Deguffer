using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// Deguffer's own source, for a test that holds a property of the whole program rather than of one
/// code path: what it never declares, never calls and never runs.
///
/// <para>Every product project, because such a rule is about Deguffer rather than about one
/// assembly, and the shell carries Win32 calls of its own.</para>
/// </summary>
internal static partial class ProductSources
{
    /// <summary>
    /// Every C# file in a product project, found by name so a project added later is covered the
    /// moment it exists.
    /// </summary>
    public static IEnumerable<string> Files() =>
        Directory.EnumerateDirectories(MarkdownGuide.RepositoryRoot, "Deguffer.*")
            .Where(project => Path.GetFileName(project) is { } name
                && !name.EndsWith(".Tests", StringComparison.Ordinal)
                && name != "Deguffer.Testing")
            .SelectMany(TestProjects.SourceFiles);

    /// <summary>
    /// Every Win32 call Deguffer declares, by the name Windows knows it under, with the file that
    /// declares it. The entry point where one is named, because that is what is called, and the
    /// method's own name otherwise.
    ///
    /// <para>Every declaration is kept, not the last of each name, so a second declaration of a call
    /// held to one file cannot hide behind the first. The declarations are read rather than the
    /// prose, so a comment explaining why Deguffer never makes a call does not read as making it. The
    /// attribute is matched under any qualified name, so <c>System.Runtime.InteropServices.DllImport</c>
    /// is no way past the sweep.</para>
    /// </summary>
    public static ILookup<string, string> Win32Imports()
    {
        var imports = new List<(string Name, string File)>();

        foreach (var file in Files())
        {
            foreach (Match match in Import().Matches(File.ReadAllText(file)))
            {
                var entryPoint = match.Groups["entry"].Value;

                imports.Add((
                    entryPoint.Length > 0 ? entryPoint : match.Groups["method"].Value,
                    Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file)));
            }
        }

        // The sweep is worth nothing if it stops matching declarations, and a count that only has to
        // beat zero would not notice. Deguffer declared 60-odd when this was written.
        Assert.True(
            imports.Count > 50,
            $"Only {imports.Count} Win32 declarations were found, so the sweep is no longer reading the source.");

        return imports.ToLookup(import => import.Name, import => import.File, StringComparer.Ordinal);
    }

    /// <summary>
    /// The lines of <paramref name="source"/> that are code: every line comment and every line of a
    /// block comment left out. For a sweep that matches a name, because the comments name what the
    /// code never does in order to explain why.
    /// </summary>
    public static IEnumerable<string> Code(string source)
    {
        var inBlock = false;

        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (inBlock)
            {
                inBlock = !trimmed.Contains("*/", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlock = !trimmed.Contains("*/", StringComparison.Ordinal);
            }
            else if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                yield return line;
            }
        }
    }

    [GeneratedRegex(
        """\[(?:[\w.]+\.)?(?:LibraryImport|DllImport)(?:Attribute)?\("[^"]+"(?:[^\]]*?EntryPoint\s*=\s*"(?<entry>[^"]+)")?[^\]]*\)\](?:\s*\[[^\]]*\])*\s*(?:public|internal|private|protected)?\s*(?:static\s+|extern\s+|partial\s+|unsafe\s+)*[^\s(]+\s+(?<method>\w+)\s*\(""",
        RegexOptions.Singleline)]
    private static partial Regex Import();
}
