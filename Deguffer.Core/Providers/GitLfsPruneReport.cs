using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>git lfs prune --dry-run --verbose</c> said it would remove: each object, by its OID, once.
/// Empty where it would remove nothing.
/// </summary>
internal sealed record GitLfsPrunePreview(IReadOnlyList<string> Objects);

/// <summary>
/// Reads what <c>git lfs prune --dry-run --verbose</c> printed with <c>lfs.forceprogress</c> on.
///
/// <para><b>Progress is forced because without it the list is lost.</b> Prune writes its report as
/// progress lines on standard output, and when that is not a terminal each line is printed once,
/// with its last update and <c>, done.</c> (<c>tasklog/log.go</c>, git-lfs 3.8.1). The verbose list
/// is one such line updated per object, so a redirected run names only the last object, and the
/// line that counts them is dropped. Forced, every update is printed and ended with a carriage
/// return and padding, and the last object appears twice: once as an update and once done.
/// Observed against git-lfs 3.8.1.</para>
///
/// <para><b>Every line is read for its shape, and the count must agree.</b> The report states how many
/// files it would remove, and a preview is returned only where exactly that many different objects
/// are listed. A git-lfs that lists fewer, as one that does not honour the forced progress does,
/// answers null, and the repository is not offered: the only other figure available is git-lfs's
/// own rounded size. Releases before 3.7.0 open every line with <c>prune: </c>, which is read
/// too.</para>
/// </summary>
internal static partial class GitLfsPruneReport
{
    private const string OldPrefix = "prune: ";

    private const string Done = ", done.";

    /// <summary>The first line, which every report that found an object prints (<c>commands/command_prune.go</c>).</summary>
    [GeneratedRegex(
        @"\A[0-9]+ local objects?, [0-9]+ retained(?:, [0-9]+ verified with remote)?(?:, [0-9]+ not on remote)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CountsLine();

    /// <summary><c>"%d files would be pruned (%s)"</c>, printed only where at least one would.</summary>
    [GeneratedRegex(@"\A(?<count>[0-9]{1,9}) files? would be pruned \([^()]+\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrunedLine();

    /// <summary><c>"\n * %s"</c> of <c>"%s (%s)"</c>: the OID, a SHA-256 in lower-case hexadecimal, and its size.</summary>
    [GeneratedRegex(@"\A \* (?<oid>[0-9a-f]{64}) \([^()]+\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectLine();

    /// <summary>The preview, or null where the output is not a whole report.</summary>
    public static GitLfsPrunePreview? TryRead(string standardOutput)
    {
        // A store with no object in it prints nothing at all.
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return new GitLfsPrunePreview([]);
        }

        var counted = false;
        int? announced = null;
        var objects = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd(' ');

            if (line.StartsWith(OldPrefix, StringComparison.Ordinal))
            {
                line = line[OldPrefix.Length..];
            }

            if (line.EndsWith(Done, StringComparison.Ordinal))
            {
                line = line[..^Done.Length];
            }

            if (CountsLine().IsMatch(line))
            {
                counted = true;
            }
            else if (PrunedLine().Match(line) is { Success: true } pruned)
            {
                announced = int.Parse(pruned.Groups["count"].ValueSpan, provider: null);
            }
            else if (ObjectLine().Match(line) is { Success: true } listed && seen.Add(listed.Groups["oid"].Value))
            {
                objects.Add(listed.Groups["oid"].Value);
            }
        }

        return counted && objects.Count == (announced ?? 0)
            ? new GitLfsPrunePreview(objects)
            : null;
    }
}
