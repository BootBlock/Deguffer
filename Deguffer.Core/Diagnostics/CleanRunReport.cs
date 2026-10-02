using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Diagnostics;

/// <summary>The build and the machine a report comes from, without naming either.</summary>
/// <param name="AppVersion">Deguffer's own version, as the About page states it.</param>
/// <param name="OperatingSystem">Windows' own description of itself, with its build number.</param>
/// <param name="Elevated">Whether Deguffer ran as administrator, which changes what it may reach.</param>
public sealed record ReportOrigin(string AppVersion, string OperatingSystem, bool Elevated);

/// <summary>
/// A finished clean written out for a GitHub issue: the verdict, every §5.6 check the run has to
/// answer for, and what each step did.
///
/// <para><b>The verdict asks the user to report a fault, and this is what they report.</b> The page
/// names each check, but a reader copying it by hand loses which provider it belonged to, what each
/// step did, and the build it came from, and those are what a diagnosis starts from.</para>
///
/// <para><b>Redacted, because it is written to be posted in public.</b> A clean's paths are full of
/// the account's name, so the profile folder is written as <c>%USERPROFILE%</c>, any other profile
/// under <c>Users</c> as <c>&lt;user&gt;</c>, and the machine's name wherever it is a path segment.
/// The report says so, and asks the reader to look before they post, because a project folder can
/// carry a name of its own that nothing here can recognise.</para>
/// </summary>
public static partial class CleanRunReport
{
    public static string Describe(IReadOnlyList<CleanupResult> results, ReportOrigin origin, IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(environment);

        var text = new StringBuilder();

        text.AppendLine("### Deguffer clean report")
            .AppendLine()
            .AppendLine($"- Deguffer: {origin.AppVersion}")
            .AppendLine($"- Windows: {origin.OperatingSystem}")
            .AppendLine($"- Running as administrator: {(origin.Elevated ? "yes" : "no")}")
            .AppendLine($"- Outcome: {RunOutcome.For(results).Statement}")
            .AppendLine()
            .AppendLine("Paths in your user folder are shown as `%USERPROFILE%`. Read the report before you "
                + "post it: it names the folders Deguffer looked at.");

        foreach (var result in results)
        {
            text.AppendLine()
                .AppendLine($"#### {result.ProviderName} (`{result.ProviderId}`)")
                .AppendLine();

            if (result.Interrupted)
            {
                text.AppendLine("Cancelled before this row finished.").AppendLine();
            }

            if (result.Verification is { } verification)
            {
                text.AppendLine($"Verification: {verification.Summary}");

                foreach (var check in verification.Failures
                             .Concat(verification.RemovedFromOutside)
                             .Concat(verification.Unverified))
                {
                    text.AppendLine($"- {check.Outcome}: `{check.Subject}`")
                        .AppendLine($"  - Found: {check.Detail}")
                        .AppendLine($"  - Protected because: {check.Reason}");
                }

                text.AppendLine();
            }

            text.AppendLine("Steps:");

            foreach (var step in result.Steps)
            {
                text.AppendLine(
                    $"- {(step.Succeeded ? "Done" : "Not done")}: {step.Description}"
                    + (string.IsNullOrEmpty(step.Message) ? string.Empty : $" — {step.Message}"));
            }
        }

        return Redact(text.ToString(), environment);
    }

    /// <summary>
    /// The report without the account's or the machine's name. The profile goes first, because the
    /// rules after it would otherwise turn it into <c>C:\Users\&lt;user&gt;</c> and hide which folder
    /// it was. Every rule is case-insensitive, because Windows paths are.
    /// </summary>
    private static string Redact(string text, IUserEnvironment environment)
    {
        var profile = Path.TrimEndingDirectorySeparator(environment.UserProfile);

        if (profile.Length > 0)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        // Another account's profile, or this one's in its short 8.3 spelling, which a temporary
        // folder's path often carries. The profiles Windows itself names are no one's, and saying
        // which one a path was in is part of the diagnosis.
        text = OtherProfile().Replace(text, @"$1<user>");

        return Segment(Segment(text, environment.UserName, "<user>"), environment.MachineName, "<machine>");
    }

    /// <summary><paramref name="name"/> replaced wherever it is a whole path segment, and nowhere else.</summary>
    private static string Segment(string text, string name, string replacement) =>
        string.IsNullOrEmpty(name)
            ? text
            : Regex.Replace(
                text,
                $@"(?<=[\\/]){Regex.Escape(name)}(?=[\\/`]|$)",
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [GeneratedRegex(
        @"(\\Users\\)(?!(?:<user>|Public|Default|Default User|All Users)(?:[\\/`\r\n]|$))[^\\/`\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherProfile();
}
