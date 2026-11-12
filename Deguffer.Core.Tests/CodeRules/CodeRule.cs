using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// One pattern the product source must not grow, with the uses that predate the rule in a baseline.
/// </summary>
/// <param name="Name">The rule's name, which is also its baseline file's name.</param>
/// <param name="Pattern">What a use looks like in <paramref name="View"/>.</param>
/// <param name="View">The parts of a file the pattern reads.</param>
/// <param name="Projects">The product projects it holds in, such as <c>Deguffer.Core</c>.</param>
/// <param name="Instead">What new code does instead, for the failure message.</param>
/// <param name="Proof">A line the pattern must match, so a pattern that silently stopped matching anything fails.</param>
internal sealed record CodeRule(
    string Name,
    Regex Pattern,
    SourceView View,
    IReadOnlyList<string> Projects,
    string Instead,
    string Proof)
{
    public int UsesIn(string maskedSource) => Pattern.Matches(maskedSource).Count;

    /// <summary>Each use in <paramref name="maskedSource"/>, at its line in the file.</summary>
    public IEnumerable<RuleViolation> ViolationsIn(string path, string maskedSource) =>
        Pattern.Matches(maskedSource).Select(match => new RuleViolation(
            path,
            maskedSource.AsSpan(0, match.Index).Count('\n') + 1,
            match.Value.Trim()));

    public bool HoldsIn(string relativePath) =>
        Projects.Any(project => relativePath.StartsWith(project + "/", StringComparison.Ordinal));
}
