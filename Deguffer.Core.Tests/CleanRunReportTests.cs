using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The report a user pastes into a GitHub issue. It has to say what a diagnosis starts from, and it
/// must not carry the account's or the machine's name into a public issue.
/// </summary>
public sealed class CleanRunReportTests : IDisposable
{
    private static readonly ReportOrigin Origin = new("1.2.3", "Microsoft Windows 10.0.26100 (X64)", Elevated: false);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void StatesTheBuildTheVerdictEachCheckAndEachStep()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var live = Path.Combine(environment.UserProfile, "AppData", "Local", "Temp", "kitprobe");

        var report = CleanRunReport.Describe([Failed(live)], Origin, environment);

        Assert.Contains("- Deguffer: 1.2.3", report, StringComparison.Ordinal);
        Assert.Contains("- Windows: Microsoft Windows 10.0.26100 (X64)", report, StringComparison.Ordinal);
        Assert.Contains("- Running as administrator: no", report, StringComparison.Ordinal);
        Assert.Contains("verification failed for Temporary files", report, StringComparison.Ordinal);
        Assert.Contains("#### Temporary files (`temp-directories`)", report, StringComparison.Ordinal);
        Assert.Contains(@"- Failed: `%USERPROFILE%\AppData\Local\Temp\kitprobe`", report, StringComparison.Ordinal);
        Assert.Contains("  - Found: MISSING — it was there before the clean.", report, StringComparison.Ordinal);
        Assert.Contains("  - Protected because: Left alone because blender was started with it.", report, StringComparison.Ordinal);
        Assert.Contains("- Done: Clear the temporary folder — Cleared.", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every spelling a clean's paths carry the account in: the profile itself, in either case, its
    /// short 8.3 name, another account's profile, and the account or machine name as a path segment
    /// anywhere else. A profile Windows itself names is no one's, and is kept for the diagnosis.
    /// </summary>
    [Theory]
    [InlineData(@"{profile}\AppData\Local\Temp\kitprobe", @"%USERPROFILE%\AppData\Local\Temp\kitprobe")]
    [InlineData(@"{PROFILE}\AppData\Local\Temp\kitprobe", @"%USERPROFILE%\AppData\Local\Temp\kitprobe")]
    [InlineData(@"C:\Users\TESTUS~1\AppData\Local\Temp\kitprobe", @"C:\Users\<user>\AppData\Local\Temp\kitprobe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\kitprobe", @"C:\Users\<user>\AppData\Local\Temp\kitprobe")]
    [InlineData(@"D:\testuser\src\obj", @"D:\<user>\src\obj")]
    [InlineData(@"\\TESTMACHINE\share\obj", @"\\<machine>\share\obj")]
    [InlineData(@"C:\Users\Public\Documents\obj", @"C:\Users\Public\Documents\obj")]
    [InlineData(@"D:\testuser-tools\obj", @"D:\testuser-tools\obj")]
    public void LeavesNoAccountOrMachineNameInAPath(string path, string expected)
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var subject = path
            .Replace("{profile}", environment.UserProfile, StringComparison.Ordinal)
            .Replace("{PROFILE}", environment.UserProfile.ToUpperInvariant(), StringComparison.Ordinal);

        var report = CleanRunReport.Describe([Failed(subject)], Origin, environment);

        Assert.Contains($"- Failed: `{expected}`", report, StringComparison.Ordinal);
        Assert.DoesNotContain(environment.UserProfile, report, StringComparison.OrdinalIgnoreCase);
    }

    private static CleanupResult Failed(string path) => new()
    {
        ProviderId = "temp-directories",
        ProviderName = "Temporary files",
        Steps = [new StepOutcome("Clear the temporary folder", Succeeded: true, 1024, Refusals.None, "Cleared.")],
        Verification = new VerificationResult
        {
            Checks =
            [
                new VerificationCheck(
                    path,
                    "Left alone because blender was started with it.",
                    VerificationOutcome.Failed,
                    "MISSING — it was there before the clean."),
            ],
        },
    };
}
