using System.Globalization;
using System.Text;

namespace Deguffer.Core.Tests;

/// <summary>One place where the shell breaks a design rule.</summary>
internal sealed record DesignViolation(string File, int Line, string Detail)
{
    public override string ToString() => $"{File}:{Line} {Detail}";
}

/// <summary>
/// A ratchet for a design rule the shell broke in many places before the rule was written. Each rule
/// keeps a count of the violations each file had then. A file that goes over its count fails, so no
/// new violation lands anywhere, and a file that goes under fails as well, so the change that fixes
/// one lowers the count in the same commit and the fixed place cannot quietly break again.
///
/// <para>A file the baseline does not name has a count of zero. A rule whose baseline is empty is a
/// plain rule with no exceptions. Raising a count to make a change pass is the maintainer's
/// decision, never a way to land work.</para>
/// </summary>
internal static class DesignBaseline
{
    public static void Hold(string rule, IEnumerable<DesignViolation> found, IReadOnlyDictionary<string, int> baseline)
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

        var message = new StringBuilder($"Design rule: {rule}");

        foreach (var (file, violations) in over)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n\n{file} has {violations.Count}, over its baseline of {baseline.GetValueOrDefault(file)}. Every instance:");

            foreach (var violation in violations.OrderBy(violation => violation.Line))
            {
                message.Append(CultureInfo.InvariantCulture, $"\n  line {violation.Line}: {violation.Detail}");
            }
        }

        foreach (var (file, count) in under)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n\n{file} has {byFile.GetValueOrDefault(file)?.Count ?? 0}, under its baseline of {count}: lower the baseline in the same change, so the fix cannot regress.");
        }

        message.Append("\n\nThe counts as they stand now, for a baseline that has only gone down:");

        foreach (var (file, violations) in byFile.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            message.Append(CultureInfo.InvariantCulture, $"\n    [\"{file}\"] = {violations.Count},");
        }

        Assert.Fail(message.ToString());
    }
}
