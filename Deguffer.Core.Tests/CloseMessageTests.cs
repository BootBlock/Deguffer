using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1: "There is one verb here, and it does not grow a second."
///
/// <para>These read Deguffer's own source rather than call anything, because what they hold is a
/// property of the whole program rather than of one code path: no test can drive the message
/// Deguffer <em>does not</em> send. A new import is how a second verb would arrive — a
/// <c>WM_QUERYENDSESSION</c> here, a <c>TerminateProcess</c> there, each reasonable on its own — and
/// this fails the moment one is declared, in any product project. The managed calls that wrap the
/// same ends need no declaration, so they are matched by name in the code.</para>
///
/// <para>The declarations are read rather than the prose, as <see cref="ProductSources.Win32Imports"/> says.</para>
/// </summary>
public sealed partial class CloseMessageTests
{
    /// <summary>
    /// Everything that would put a message in another program's queue, end a process, or push its
    /// pages out. Each is refused by §7.2.1 or by §2, and none of them is declared.
    ///
    /// <para><c>ExitWindowsEx</c> is not here: §7 lets the user choose to log off, restart or shut
    /// down once a clean has finished. It is held to one file and to Windows asking every program to
    /// close, by <see cref="TheOnlySessionEndIsAskedOfWindowsAndNeverForced"/>.</para>
    ///
    /// <para>Nor is <c>TerminateJobObject</c>: a tool Deguffer started itself is ended through the job
    /// it was started in, which is held to jobs Deguffer made by
    /// <see cref="TheOnlyJobEndedIsOneDegufferMadeForItsOwnTools"/>.</para>
    /// </summary>
    private static readonly string[] NeverDeclared =
    [
        "SendMessage", "SendMessageW", "SendMessageTimeout", "SendMessageTimeoutW", "SendNotifyMessage",
        "SendNotifyMessageW", "PostThreadMessage", "PostThreadMessageW", "SendInput", "keybd_event",
        "mouse_event", "EndTask", "TerminateProcess", "NtTerminateProcess",
        "InitiateShutdown", "InitiateShutdownW", "InitiateSystemShutdown", "InitiateSystemShutdownW",
        "InitiateSystemShutdownEx", "InitiateSystemShutdownExW", "GenerateConsoleCtrlEvent", "AttachConsole",
        "RmShutdown", "RmRestart", "EmptyWorkingSet", "SetProcessWorkingSetSize",
        "SetProcessWorkingSetSizeEx", "NtSetSystemInformation", "NtSuspendProcess", "DebugActiveProcess",
    ];

    /// <summary>The one call Deguffer makes into another program's message queue.</summary>
    private const string TheOnlyPost = "PostMessageW";

    /// <summary>The one call that ends the Windows session, which the user chooses to follow a clean.</summary>
    private const string TheOnlySessionEnd = "ExitWindowsEx";

    /// <summary>The one call that ends processes, all of them tools Deguffer started.</summary>
    private const string TheOnlyJobEnd = "TerminateJobObject";

    [Fact]
    public void NoCallThatEndsOrCommandsAnotherProgramIsDeclared()
    {
        var declared = ProductSources.Win32Imports();

        foreach (var banned in NeverDeclared)
        {
            Assert.False(
                declared.Contains(banned),
                $"{banned} is declared in {string.Join(", ", declared[banned])}. §7.2.1 has one verb: "
                + "the program's own close, posted as WM_CLOSE, and §2 rules out trimming a working "
                + "set or touching a memory list.");
        }
    }

    /// <summary>
    /// The one post, declared once, in the one file, and reached through a method that takes no
    /// message from its caller.
    /// </summary>
    [Fact]
    public void TheOnlyMessageDegufferPostsIsWindowClose()
    {
        var declared = ProductSources.Win32Imports();

        Assert.Equal(
            Path.Combine("Deguffer.Core", "Memory", "WindowCalls.cs"),
            Assert.Single(declared[TheOnlyPost]));

        var source = File.ReadAllText(Path.Combine(Core, "Memory", "WindowCalls.cs"));

        // WM_CLOSE is 0x0010, and it is the only message named here.
        Assert.Contains("private const uint WindowClose = 0x0010;", source, StringComparison.Ordinal);

        var post = Assert.Single(PostCall().Matches(source));

        Assert.Equal("WindowClose", post.Groups["message"].Value);
    }

    /// <summary>
    /// Logging off, restarting and shutting down go through one declaration, in one file, and every
    /// call names one plain flag: <c>EWX_LOGOFF</c>, <c>EWX_REBOOT</c> or <c>EWX_POWEROFF</c>, never
    /// combined with <c>EWX_FORCE</c> or <c>EWX_FORCEIFHUNG</c>. Without those, Windows asks every
    /// program to close and any of them may refuse, which is the line §7.2.1 draws for its own verb.
    /// </summary>
    [Fact]
    public void TheOnlySessionEndIsAskedOfWindowsAndNeverForced()
    {
        var declared = ProductSources.Win32Imports();
        var file = Path.Combine("Deguffer.Core", "Execution", "WindowsSession.cs");

        Assert.Equal(file, Assert.Single(declared[TheOnlySessionEnd]));

        var source = File.ReadAllText(Path.Combine(MarkdownGuide.RepositoryRoot, file));

        Assert.Contains("private const uint ExitLogOff = 0x0;", source, StringComparison.Ordinal);
        Assert.Contains("private const uint ExitReboot = 0x2;", source, StringComparison.Ordinal);
        Assert.Contains("private const uint ExitPowerOff = 0x8;", source, StringComparison.Ordinal);

        // Every line of code that names the call is the declaration or a call with one plain flag.
        // Anything else, a flag combined with a force flag or the call taken as a delegate included,
        // is a line here that matches neither. Comments are left out, because they name it in prose.
        var code = ProductSources.Code(source)
            .Where(line => SessionEndMention().IsMatch(line))
            .ToList();

        Assert.Equal(4, code.Count);
        Assert.Single(code, line => SessionEndDeclaration().IsMatch(line));
        Assert.Equal(
            ["ExitLogOff", "ExitPowerOff", "ExitReboot"],
            code.Select(line => SessionEndCall().Match(line))
                .Where(call => call.Success)
                .Select(call => call.Groups["flags"].Value)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A job is ended by one declaration, called from one type, and that type's jobs come only from
    /// Windows making a new one. A job made empty holds nothing but what Deguffer starts in it, so
    /// ending one can reach no program the user runs. A job opened by name, or a handle taken from
    /// elsewhere, is how that would stop being true, and both would be a second way into the type.
    /// </summary>
    [Fact]
    public void TheOnlyJobEndedIsOneDegufferMadeForItsOwnTools()
    {
        var declared = ProductSources.Win32Imports();

        Assert.Equal(Path.Combine("Deguffer.Core", "Safety", "ToolProcessNative.cs"), Assert.Single(declared[TheOnlyJobEnd]));
        Assert.False(declared.Contains("OpenJobObjectW") || declared.Contains("OpenJobObject"), "A job is opened rather than made.");

        var callers = ProductSources.Files()
            .Where(file => TerminateCall().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file));

        Assert.Equal(Path.Combine("Deguffer.Core", "Safety", "ToolJob.cs"), Assert.Single(callers));

        var job = File.ReadAllText(Path.Combine(Core, "Safety", "ToolJob.cs"));

        Assert.Contains("private ToolJob(SafeJobHandle handle)", job, StringComparison.Ordinal);
        Assert.Single(NewJob().Matches(job));
        Assert.Contains("var handle = ToolProcessNative.CreateJobObject(0, 0);", job, StringComparison.Ordinal);
    }

    /// <summary>
    /// The managed routes to the same ends, which no import declares and so
    /// <see cref="NoCallThatEndsOrCommandsAnotherProgramIsDeclared"/> cannot see: <c>Process.Kill</c>
    /// is <c>TerminateProcess</c>, <c>CloseMainWindow</c> is a posted close Deguffer did not choose the
    /// window for, <c>SendKeys</c> is <c>SendInput</c>, WMI's <c>Win32_Process.Terminate</c> arrives
    /// through <c>System.Management</c>, and <c>NativeLibrary</c> reaches any export by name without a
    /// declaration at all. <c>GetProcessById</c> hands over another program's <c>Process</c>, which is
    /// where a <c>Kill</c> or a <c>CloseMainWindow</c> would start.
    ///
    /// <para>Matched on code, not comments, because the comments explain why these are not used.</para>
    /// </summary>
    [Fact]
    public void NoManagedRouteEndsOrCommandsAnotherProgram()
    {
        var used = ProductSources.Files()
            .SelectMany(file => ProductSources.Code(File.ReadAllText(file))
                .SelectMany(line => ManagedRoute().Matches(line))
                .Select(match => (Route: match.Groups["route"].Value, File: Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file))))
            .Where(use => !NativeLibraryAllowance(use.Route, use.File))
            .Select(use => $"{use.Route} in {use.File}")
            .ToList();

        Assert.True(
            used.Count == 0,
            $"{string.Join(", ", used)}. §7.2.1 has one verb: the program's own close, posted as WM_CLOSE "
            + "to a window Deguffer chose.");
    }

    /// <summary>
    /// The one <c>NativeLibrary</c> use: <c>SRRemoveRestorePoint</c>, which Microsoft says is loaded
    /// by name rather than linked, and which removes a restore point rather than reaching any program.
    /// The allowance is held to that file and that export, so a second export loaded there is a
    /// second route and fails.
    /// </summary>
    [Fact]
    public void TheOnlyLibraryLoadedByNameRemovesARestorePoint()
    {
        var code = string.Join('\n', ProductSources.Code(File.ReadAllText(Path.Combine(MarkdownGuide.RepositoryRoot, RestorePointCalls))));

        Assert.Equal(["\"SRRemoveRestorePoint\""], ExportLoaded().Matches(code).Select(match => match.Groups["export"].Value.Trim()));
    }

    /// <summary>The one file allowed <c>NativeLibrary</c>, for the reason <see cref="TheOnlyLibraryLoadedByNameRemovesARestorePoint"/> gives.</summary>
    private static readonly string RestorePointCalls = Path.Combine("Deguffer.Core", "SystemProtection", "RestorePointCalls.cs");

    private static bool NativeLibraryAllowance(string route, string file) =>
        route == "NativeLibrary" && file == RestorePointCalls;

    private static string Core =>
        Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.Core");

    [GeneratedRegex(@"(?<route>\bKill\b|\bCloseMainWindow\b|\bGetProcessById\b|\bNativeLibrary\b|\bSystem\.Management\b|\bSendKeys\b)")]
    private static partial Regex ManagedRoute();

    /// <summary>Every export taken, by the text of its name argument, so one named by a variable is seen too.</summary>
    [GeneratedRegex(@"\bNativeLibrary\.(?:Try)?GetExport\s*\(\s*\w+\s*,\s*(?<export>[^,)]+)")]
    private static partial Regex ExportLoaded();

    [GeneratedRegex(@"\bPostMessage\(\s*\w+\s*,\s*(?<message>\w+)")]
    private static partial Regex PostCall();

    [GeneratedRegex(@"\bExitWindowsEx\s*\(\s*(?<flags>Exit(?:LogOff|Reboot|PowerOff))\s*,\s*ReasonPlanned\s*\)")]
    private static partial Regex SessionEndCall();

    [GeneratedRegex(@"\bstatic\s+partial\s+bool\s+ExitWindowsEx\s*\(\s*uint\s+flags\s*,\s*uint\s+reason\s*\)\s*;")]
    private static partial Regex SessionEndDeclaration();

    [GeneratedRegex(@"\bExitWindowsEx\b")]
    private static partial Regex SessionEndMention();

    /// <summary>A call, which the declaration's <c>bool</c> before the name tells apart from it.</summary>
    [GeneratedRegex(@"(?<!\bbool\s+)\bTerminateJobObject\s*\(")]
    private static partial Regex TerminateCall();

    [GeneratedRegex(@"\bnew\s+ToolJob\s*\(")]
    private static partial Regex NewJob();
}
