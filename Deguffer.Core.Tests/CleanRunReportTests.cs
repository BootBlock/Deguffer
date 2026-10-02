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

        var report = CleanRunReport.Describe([Failed(live, "Clear the temporary folder", "Cleared.")], Origin, environment);

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
    /// Every spelling a clean's paths carry the account in, in each place a report holds a path: in
    /// backticks, at the end of a line, and quoted inside a tool's message with more text after it.
    /// What a rule must not touch is here too: a profile Windows itself names, a folder whose name
    /// only starts with the account's, and a source folder called <c>Users</c>. A hyphen does not
    /// join words, so a folder named after the account and a tool loses the account's half.
    /// </summary>
    [Theory]
    [InlineData(@"{profile}\AppData\Local\Temp\kitprobe", @"%USERPROFILE%\AppData\Local\Temp\kitprobe")]
    [InlineData(@"{PROFILE}\AppData\Local\Temp\kitprobe", @"%USERPROFILE%\AppData\Local\Temp\kitprobe")]
    [InlineData(@"{profile/}/AppData/Local/Temp/kitprobe", @"%USERPROFILE%/AppData/Local/Temp/kitprobe")]
    [InlineData(@"{profile}\OneDrive - Contoso Ltd\Documents", @"%USERPROFILE%\OneDrive - <organisation>\Documents")]
    [InlineData(@"C:\Users\TESTUS~1\AppData\Local\Temp\kitprobe", @"C:\Users\<user>\AppData\Local\Temp\kitprobe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp", @"C:\Users\<user>\AppData\Local\Temp")]
    [InlineData(@"C:/Users/someone/AppData/Local/Temp", @"C:/Users/<user>/AppData/Local/Temp")]
    [InlineData(@"C:\Users\Joe Smith\AppData\Local\Temp", @"C:\Users\<user>\AppData\Local\Temp")]
    [InlineData(@"C:\Users\Joe Smith", @"C:\Users\<user>")]
    [InlineData(@"D:\testuser\src\obj", @"D:\<user>\src\obj")]
    [InlineData(@"D:\work\testuser", @"D:\work\<user>")]
    [InlineData(@"\\TESTMACHINE\share\obj", @"\\<machine>\share\obj")]
    [InlineData(@"\\TESTMACHINE.corp.example\share\obj", @"\\<machine>\share\obj")]
    [InlineData(@"D:\Joe Smith\Videos\CacheClip", @"<personal folder>\CacheClip")]
    [InlineData(@"C:\Users\Public\Documents\obj", @"C:\Users\Public\Documents\obj")]
    [InlineData(@"D:\testuser-tools\obj", @"D:\<user>-tools\obj")]
    [InlineData(@"D:\testusers\obj", @"D:\testusers\obj")]
    [InlineData(@"D:\src\Users\UserController.cs", @"D:\src\Users\UserController.cs")]
    public void LeavesNoAccountOrMachineNameInAPath(string path, string expected)
    {
        var environment = new FakeUserEnvironment(_temp.Path).WithPersonalFolderAt(@"D:\Joe Smith\Videos");
        var subject = path
            .Replace("{profile/}", environment.UserProfile.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{profile}", environment.UserProfile, StringComparison.Ordinal)
            .Replace("{PROFILE}", environment.UserProfile.ToUpperInvariant(), StringComparison.Ordinal);

        var report = CleanRunReport.Describe(
            [Failed(subject, $"Clear — {subject}", message: null), Failed(subject, "Clear", $"'{subject}' is denied.")],
            Origin,
            environment);

        Assert.Contains($"- Failed: `{expected}`", report, StringComparison.Ordinal);
        Assert.Contains($"- Done: Clear — {expected}{Environment.NewLine}", report, StringComparison.Ordinal);
        Assert.Contains($"- Done: Clear — '{expected}' is denied.", report, StringComparison.Ordinal);
        Assert.DoesNotContain(environment.UserProfile, report, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The names, wherever a tool's message puts them: an account written as <c>MACHINE\user</c>
    /// outside any path, a path followed by prose or punctuation, a share in an extended or
    /// forward-slash form, and a work OneDrive's organisation outside its folder. What a rule must not
    /// eat is here too: the sentence after a profile Windows itself names, and a drive that is only a
    /// personal folder's root.
    /// </summary>
    [Theory]
    [InlineData(@"Access is denied for TESTMACHINE\testuser (owner testuser).", @"Access is denied for <machine>\<user> (owner <user>).")]
    [InlineData(@"Failed \\?\UNC\TESTMACHINE.corp.example\share\x", @"Failed \\?\UNC\<machine>\share\x")]
    [InlineData(@"Failed //TESTMACHINE.corp.example/share/x", @"Failed //<machine>/share/x")]
    [InlineData(@"Could not find {profile}.", @"Could not find %USERPROFILE%.")]
    [InlineData(@"In {profile}, 3 files locked", @"In %USERPROFILE%, 3 files locked")]
    [InlineData(@"Could not find E:\Joe Smith\Documents.", @"Could not find <personal folder>.")]
    [InlineData(@"OneDrive - Contoso is syncing", @"OneDrive - <organisation> is syncing")]
    [InlineData(@"Deleted {profile}\Contoso\Finance - Documents\x", @"Deleted %USERPROFILE%\<organisation>\Finance - Documents\x")]
    [InlineData(@"Kept C:\Users\Public because it is shared", @"Kept C:\Users\Public because it is shared")]
    [InlineData(@"Deleted D:\dev\cache — Process ID: 4", @"Deleted D:\dev\cache — Process ID: 4")]
    [InlineData(@"Owner: TESTDOMAIN\testuser; UPN testuser@testdomain.example", @"Owner: <domain>\<user>; UPN <user>@<domain>.example")]
    [InlineData(@"Skipped C:\Users\O'Brien\AppData\x", @"Skipped C:\Users\<user>\AppData\x")]
    [InlineData(@"npm ERR! path /mnt/c/Users/alice/AppData", @"npm ERR! path /mnt/c/Users/<user>/AppData")]
    [InlineData(@"Open \Device\HarddiskVolume3\Users\alice\AppData", @"Open \Device\HarddiskVolume3\Users\<user>\AppData")]
    [InlineData(@"Failed \\TESTMACHINE\c$\Users\alice\AppData", @"Failed \\<machine>\c$\Users\<user>\AppData")]
    [InlineData(@"Removed testuser-cache and testuser_backup", @"Removed <user>-cache and <user>_backup")]
    [InlineData(@"Synced Fabrikam - Documents", @"Synced <organisation> - Documents")]
    [InlineData(@"Your personal data was kept", @"Your personal data was kept")]
    public void LeavesNoNameInAMessage(string message, string expected)
    {
        var environment = new FakeUserEnvironment(_temp.Path)
            .WithPersonalFolderAt(@"E:\Joe Smith\Documents")
            .WithPersonalFolderAt(@"D:\");
        environment
            .WithPersonalFolderAt(Path.Combine(environment.UserProfile, "OneDrive - Contoso"))
            .WithPersonalFolderAt(Path.Combine(environment.UserProfile, "OneDrive - Personal"))
            .WithPersonalFolderAt(Path.Combine(environment.UserProfile, "OneDrive - Fabrikam", "Documents"));

        var report = CleanRunReport.Describe(
            [Failed(@"C:\Temp\x", "Clear", message.Replace("{profile}", environment.UserProfile, StringComparison.Ordinal))],
            Origin,
            environment);

        Assert.Contains($"- Done: Clear — {expected}{Environment.NewLine}", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// What one rule writes, no later rule reads. An organisation that happens to be called
    /// <c>User</c> would otherwise turn the <c>&lt;user&gt;</c> another account's profile became into
    /// <c>&lt;&lt;organisation&gt;&gt;</c>.
    /// </summary>
    [Fact]
    public void LeavesAReplacementAloneWhateverANameIsCalled()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        environment.WithPersonalFolderAt(Path.Combine(environment.UserProfile, "OneDrive - User"));

        var report = CleanRunReport.Describe([Failed(@"C:\Temp\x", "Clear", @"Skipped C:\Users\alice\x")], Origin, environment);

        Assert.Contains($@"- Done: Clear — Skipped C:\Users\<user>\x{Environment.NewLine}", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile ends where a folder name ends. A neighbouring profile whose name only starts with
    /// this one's is another account's, and reading it as this one's would both mislabel it and leave
    /// the rest of its name in the report.
    /// </summary>
    [Fact]
    public void DoesNotReadAProfileThatOnlyStartsWithThisOnesAsThisOne()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var neighbour = environment.UserProfile + "by";

        var report = CleanRunReport.Describe([Failed(Path.Combine(neighbour, "obj"), "Clear", "Cleared.")], Origin, environment);

        Assert.DoesNotContain("%USERPROFILE%by", report, StringComparison.Ordinal);
        Assert.Contains(@"\profileby\obj", report, StringComparison.Ordinal);
    }

    private static CleanupResult Failed(string path, string step, string? message) => new()
    {
        ProviderId = "temp-directories",
        ProviderName = "Temporary files",
        Steps = [new StepOutcome(step, Succeeded: true, 1024, Refusals.None, message)],
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
