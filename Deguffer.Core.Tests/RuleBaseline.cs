using System.Globalization;
using System.Text;

namespace Deguffer.Core.Tests;

/// <summary>
/// One place where the product source breaks a rule. <paramref name="Line"/> is 0 where the place is
/// not a line, such as a Win32 call declared in a second file.
/// </summary>
internal sealed record RuleViolation(string File, int Line, string Detail)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line} {Detail}" : $"{File} {Detail}";
}

/// <summary>
/// A ratchet for a rule the product source broke in many places before the rule was written, the
/// design rules and the code rules alike. Each rule keeps a count of the violations each file (or
/// other key, such as a Win32 call) had then. A key that goes over its count fails, so no new
/// violation lands anywhere, and a key that goes under fails as well, so the change that fixes one
/// lowers the count in the same commit and the fixed place cannot quietly break again.
///
/// <para>A key the baseline does not name has a count of zero. A rule whose baseline is empty is a
/// plain rule with no exceptions. Raising a count to make a change pass is the maintainer's
/// decision, never a way to land work.</para>
/// </summary>
internal static class RuleBaseline
{
    public static void Hold(string rule, IEnumerable<RuleViolation> found, IReadOnlyDictionary<string, int> baseline)
    {
        var byFile = found.GroupBy(violation => violation.File, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var over = byFile
            .Where(file => file.Value.Count > baseline.GetValueOrDefault(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .ToList();

        var under = baseline
            .Where(file => byFile.GetValueOrDefault(file.Key)?.Count is var count && (count ?? 0) < file.Value)
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .ToList();

        if (over.Count == 0 && under.Count == 0)
        {
            return;
        }

        var message = new StringBuilder($"Rule: {rule}");

        foreach (var (file, violations) in over)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n\n{file} has {violations.Count}, over its baseline of {baseline.GetValueOrDefault(file)}. Every instance:");

            foreach (var violation in violations.OrderBy(violation => violation.Line))
            {
                if (violation.Line > 0)
                {
                    message.Append(CultureInfo.InvariantCulture, $"\n  line {violation.Line}: {violation.Detail}");
                }
                else
                {
                    message.Append(CultureInfo.InvariantCulture, $"\n  {violation.Detail}");
                }
            }
        }

        foreach (var (file, count) in under)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n\n{file} has {byFile.GetValueOrDefault(file)?.Count ?? 0}, under its baseline of {count}: lower the baseline in the same change, so the fix cannot regress.");
        }

        message.Append("\n\nThe counts as they stand now, for a baseline that has only gone down:");

        foreach (var (file, violations) in byFile.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            message.Append(CultureInfo.InvariantCulture, $"\n    {file}: {violations.Count}");
        }

        Assert.Fail(message.ToString());
    }
}
