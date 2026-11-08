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
/// path nothing has described yet, and is left alone.</para>
///
/// <para>The removal's <c>IFileSystem</c> seam has no link check to sweep for: its
/// <c>ProbeEntry(path, out isDirectory, out isLink)</c> is the only way it answers one, so the
/// compiler holds that rule there. What it cannot see is a removal reaching past the seam to
/// <c>LongPath</c>, which this sweep reads for, and <see cref="NoRemovalAsksLongPathAboutALinkPastItsSeam"/>
/// reads for a link check anywhere in a removal.</para>
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
    /// A removal asks whether a path is a link only through its <c>IFileSystem</c> seam, whose one
    /// answer comes from the read that gave the presence and the kind. <c>LongPath.IsReparsePoint</c>
    /// past the seam is a second read of a path the seam's probe already described, and one that a
    /// test wrapping the seam never sees.
    /// </summary>
    [Fact]
    public void NoRemovalAsksLongPathAboutALinkPastItsSeam()
    {
        var removals = 0;
        var bypasses = new List<string>();

        foreach (var file in ProductSources.Files())
        {
            var code = ProductSources.Code(File.ReadAllText(file)).ToList();

            if (!code.Any(line => FileSystemSeam().IsMatch(line)))
            {
                continue;
            }

            removals++;

            var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');

            bypasses.AddRange(code
                .Where(line => AnyLinkCheck().IsMatch(line))
                .Select(line => $"{name}: {line.Trim()}"));
        }

        // The sweep is worth nothing if it stops finding the removal's files. Fifteen named the seam
        // when this was written.
        Assert.True(removals > 10, $"Only {removals} files name IFileSystem, so the sweep is no longer reading the source.");
        Assert.Empty(bypasses);
    }

    /// <summary>
    /// A probe that reads a path's attributes: the three-state probes, the two-state forms built on
    /// them, and the base plan that asks one.
    /// </summary>
    [GeneratedRegex(@"(?:\bLongPath\s*\.\s*(?:Probe\w*|DirectoryExists|DirectoryMayExist|FileExists|FileMayExist|EntryExists)|\bNothingToPlanFor)\(\s*(?<path>[\w.]+)\s*[,)]")]
    private static partial Regex Probe();

    [GeneratedRegex(@"\bLongPath\s*\.\s*IsReparsePoint\(\s*(?<path>[\w.]+)\s*\)")]
    private static partial Regex LinkCheck();

    /// <summary>A call to <c>LongPath.IsReparsePoint</c>, whatever its argument is.</summary>
    [GeneratedRegex(@"\bLongPath\s*\.\s*IsReparsePoint\s*\(")]
    private static partial Regex AnyLinkCheck();

    /// <summary>A line of code that names the removal's file-system seam.</summary>
    [GeneratedRegex(@"\bIFileSystem\b")]
    private static partial Regex FileSystemSeam();

    /// <summary>
    /// A line that opens a member, which starts a new window: a probe in one method says nothing about
    /// a link check in another, such as a presence probe beside the plan.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:public|private|internal|protected)\s")]
    private static partial Regex MemberDeclaration();
}
