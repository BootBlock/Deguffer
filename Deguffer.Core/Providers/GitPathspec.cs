using System.Text;

namespace Deguffer.Core.Providers;

/// <summary>
/// How a repository-relative directory is put to git so that git finds every tracked file Windows
/// would place inside it, whatever case the index spells the directory in.
///
/// <para>Git matches a pathspec against the index case-sensitively whatever <c>core.ignorecase</c>
/// says, and the index can spell a directory differently from the disk: after a rename that changed
/// only case, or in a repository created on Linux. Asked in the disk's spelling, git then answers
/// "nothing tracked" successfully for a directory full of tracked files. <c>icase</c> magic fixes
/// that for ASCII letters, and only for them: git folds no other letter, so <c>übung</c> in the index
/// is still not found by <c>Übung</c>.</para>
///
/// <para>So a path in ASCII is asked about literally, and a path with any other character is asked
/// about with a glob that stands <c>*</c> in for each run of non-ASCII characters. That glob matches
/// more than the directory, never less, because <c>*</c> does not cross a <c>/</c>. The caller
/// attributes each listed path to the candidate it lies inside with
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, which folds every letter, and so discards
/// what the glob matched beyond the candidate.</para>
/// </summary>
internal static class GitPathspec
{
    /// <summary>
    /// For a path in ASCII. <c>literal</c>, because these are paths rather than patterns: without it a
    /// name holding a glob metacharacter, such as <c>App[Legacy]</c>, is also a pattern over other
    /// directories.
    /// </summary>
    private const string Literal = ":(literal,icase)";

    /// <summary>For a path with a character outside ASCII, which <c>icase</c> cannot fold.</summary>
    private const string Glob = ":(glob,icase)";

    /// <summary>
    /// The pathspec for <paramref name="relativeDirectory"/>, a directory relative to the repository
    /// root with forward slashes.
    /// </summary>
    public static string For(string relativeDirectory)
    {
        if (Ascii.IsValid(relativeDirectory))
        {
            return Literal + relativeDirectory;
        }

        var spelling = new StringBuilder(Glob.Length + relativeDirectory.Length * 2 + 3).Append(Glob);
        var inRun = false;

        foreach (var character in relativeDirectory)
        {
            if (!char.IsAscii(character))
            {
                // One * for the whole run, so a surrogate pair or a word in another script is one
                // wildcard rather than a run of them.
                if (!inRun)
                {
                    spelling.Append('*');
                    inRun = true;
                }

                continue;
            }

            inRun = false;

            // A glob character in the name is matched as itself by a class holding only it. Not by a
            // backslash: git for Windows reads a backslash in a pathspec as a directory separator.
            if (character is '[' or ']' or '*' or '?')
            {
                spelling.Append('[').Append(character).Append(']');
            }
            else
            {
                spelling.Append(character);
            }
        }

        // A glob names a path, not a directory and what is in it, so the contents are matched
        // explicitly. A literal pathspec matches a directory's contents by itself.
        return spelling.Append("/**").ToString();
    }
}
