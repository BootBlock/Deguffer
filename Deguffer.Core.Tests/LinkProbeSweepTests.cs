using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// No product code asks whether a path is a link after a probe of the same path has already read its
/// attributes.
///
/// <para><b>Read from the source, because both forms compile and agree on a quiet disk.</b> A probe
/// followed by <c>LongPath.IsReparsePoint</c> is two attribute reads of one path, and the two can
/// describe different entries: a folder replaced by a junction between them passes the presence check
/// as the folder and the link check as the junction. <c>ProbeDirectory(path, out isLink)</c>,
/// <c>ProbeFile(path, out isLink)</c> and <c>NothingToPlanFor(root, absent, out isLink)</c> answer
/// both from one read.</para>
///
/// <para>A link check with no probe before it in the same member is a different question, asked of a
/// path nothing has described yet, and is left alone. So is the removal's <c>IFileSystem</c> seam,
/// whose link check also answers for a file at a directory's path.</para>
/// </summary>
public sealed partial class LinkProbeSweepTests
{
    [Fact]
    public void NoLinkCheckRereadsAPathItsMemberAlreadyProbed()
    {
        var rereads = new List<string>();

        // LongPath itself is left out, because it defines both.
        foreach (var file in ProductSources.Files()
            .Where(file => !Path.GetFileName(file).Equals("LongPath.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');
            var member = new List<string>();

            foreach (var line in ProductSources.Code(File.ReadAllText(file)))
            {
                if (MemberDeclaration().IsMatch(line))
                {
                    member.Clear();
                }

                foreach (Match check in LinkCheck().Matches(line))
                {
                    // The lines before, and this line up to the check, joined so a call split across
                    // lines still reads as one.
                    var before = string.Join('\n', member) + '\n' + line[..check.Index];

                    var path = check.Groups["path"].Value;

                    if (Probe().Matches(before).Any(probe => probe.Groups["path"].Value == path))
                    {
                        rereads.Add($"{name}: {line.Trim()}");
                    }
                }

                member.Add(line);
            }
        }

        Assert.Empty(rereads);
    }

    /// <summary>
    /// A probe that reads a path's attributes: the three-state probes, the two-state forms built on
    /// them, and the base plan that asks one.
    /// </summary>
    [GeneratedRegex(@"(?:\bLongPath\s*\.\s*(?:Probe\w*|DirectoryExists|DirectoryMayExist|FileExists|FileMayExist|EntryExists)|\bNothingToPlanFor)\(\s*(?<path>[\w.]+)\s*[,)]")]
    private static partial Regex Probe();

    [GeneratedRegex(@"\bLongPath\s*\.\s*IsReparsePoint\(\s*(?<path>[\w.]+)\s*\)")]
    private static partial Regex LinkCheck();

    /// <summary>
    /// A line that opens a member, which starts a new window: a probe in one method says nothing about
    /// a link check in another, such as a presence probe beside the plan.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:public|private|internal|protected)\s")]
    private static partial Regex MemberDeclaration();
}
