using System.Globalization;
using System.Text;
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
/// answer for, and what each step did and how long it took.
///
/// <para><b>The verdict asks the user to report a fault, and this is what they report.</b> The page
/// names each check, but a reader copying it by hand loses which provider it belonged to, what each
/// step did, and the build it came from, and those are what a diagnosis starts from.</para>
///
/// <para><b>Redacted, because it is written to be posted in public.</b> A clean's paths are full of
/// the account's name, so <see cref="ReportRedaction"/> takes it out, and the machine's with it.
/// The report says what it did, and asks the reader to look before they post, because a project
/// folder can carry a name of its own that nothing here can recognise.</para>
/// </summary>
public static class CleanRunReport
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
            .AppendLine("Your user folder is shown as `%USERPROFILE%`, and account, machine and organisation "
                + "names as `<user>`, `<machine>` and `<organisation>`. Read the report before you post it: "
                + "it names the folders Deguffer looked at.");

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

                foreach (var check in verification.Unpassed)
                {
                    text.AppendLine($"- {check.Outcome}: `{check.Subject}`")
                        .AppendLine($"  - Found: {check.Detail}")
                        .AppendLine($"  - Protected because: {check.Reason}");
                }

                text.AppendLine();
            }

            text.AppendLine($"Took {Seconds(result.Duration)}.")
                .AppendLine()
                .AppendLine("Steps:");

            foreach (var step in result.Steps)
            {
                text.AppendLine(
                    $"- {(step.Succeeded ? "Done" : "Not done")}: {step.Description}"
                    + (string.IsNullOrEmpty(step.Message) ? string.Empty : $" — {step.Message}"));

                AppendTiming(text, step);
            }
        }

        return new ReportRedaction(environment).Apply(text.ToString());
    }

    /// <summary>
    /// Where one step spent its time, so a slow clean reported from a user's machine names the step and
    /// the pass that were slow. Figures alone, and never a path, so redaction has nothing here to miss.
    /// </summary>
    private static void AppendTiming(StringBuilder text, StepOutcome step)
    {
        var timing = step.Timing;

        text.AppendLine(
            $"  - Took {Seconds(timing.Elapsed)}: use check {Seconds(timing.UseCheck)}, "
            + $"look {Seconds(timing.Look)}, the rest {Seconds(timing.Acting)}");

        if (step.Passes is { } passes)
        {
            text.AppendLine(
                $"  - Removal: walk {Pass(passes.Walk)}, files {Pass(passes.Files)}, folders {Pass(passes.Folders)}");
        }
    }

    private static string Pass(PassTiming pass) =>
        string.Create(CultureInfo.InvariantCulture, $"{Seconds(pass.Elapsed)} over {pass.Entries:N0}");

    /// <summary>Invariant, because the report is read by whoever answers the issue, not by the machine it came from.</summary>
    private static string Seconds(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.000} s");
}
