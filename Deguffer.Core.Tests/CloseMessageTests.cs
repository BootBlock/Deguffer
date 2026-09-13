using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1: "There is one verb here, and it does not grow a second."
///
/// <para>These read Core's own source rather than call anything, because what they hold is a
/// property of the whole assembly rather than of one code path: no test can drive the message
/// Deguffer <em>does not</em> send. A new import is how a second verb would arrive — a
/// <c>WM_QUERYENDSESSION</c> here, a <c>TerminateProcess</c> there, each reasonable on its own — and
/// this fails the moment one is declared.</para>
///
/// <para>The declarations are read rather than the prose, so a comment explaining why Deguffer never
/// terminates anything does not read as a termination.</para>
/// </summary>
public sealed partial class CloseMessageTests
{
    /// <summary>
    /// Everything that would put a message in another program's queue, end a process, or push its
    /// pages out. Each is refused by §7.2.1 or by §2, and none of them is declared.
    /// </summary>
    private static readonly string[] NeverDeclared =
    [
        "SendMessage", "SendMessageW", "SendMessageTimeout", "SendMessageTimeoutW", "SendNotifyMessage",
        "SendNotifyMessageW", "PostThreadMessage", "PostThreadMessageW", "SendInput", "keybd_event",
        "mouse_event", "EndTask", "TerminateProcess", "NtTerminateProcess", "TerminateJobObject",
        "ExitWindowsEx", "InitiateShutdown", "GenerateConsoleCtrlEvent", "AttachConsole",
        "RmShutdown", "RmRestart", "EmptyWorkingSet", "SetProcessWorkingSetSize",
        "SetProcessWorkingSetSizeEx", "NtSetSystemInformation", "NtSuspendProcess", "DebugActiveProcess",
    ];

    /// <summary>The one call Deguffer makes into another program's message queue.</summary>
    private const string TheOnlyPost = "PostMessageW";

    [Fact]
    public void NoCallThatEndsOrCommandsAnotherProgramIsDeclared()
    {
        var declared = Imports();

        foreach (var banned in NeverDeclared)
        {
            Assert.False(
                declared.TryGetValue(banned, out var file),
                $"{banned} is declared in {file}. §7.2.1 has one verb: the program's own close, posted "
                + "as WM_CLOSE, and §2 rules out trimming a working set or touching a memory list.");
        }
    }

    /// <summary>
    /// The one post, declared once, in the one file, and reached through a method that takes no
    /// message from its caller.
    /// </summary>
    [Fact]
    public void TheOnlyMessageDegufferPostsIsWindowClose()
    {
        var declared = Imports();

        Assert.True(declared.ContainsKey(TheOnlyPost), $"{TheOnlyPost} is no longer declared anywhere.");
        Assert.Equal(Path.Combine("Memory", "WindowCalls.cs"), declared[TheOnlyPost]);

        var source = File.ReadAllText(Path.Combine(Core, "Memory", "WindowCalls.cs"));

        // WM_CLOSE is 0x0010, and it is the only message named here.
        Assert.Contains("private const uint WindowClose = 0x0010;", source, StringComparison.Ordinal);

        var posts = PostCall().Matches(source);

        Assert.Equal(1, posts.Count);
        Assert.Equal("WindowClose", posts[0].Groups["message"].Value);
    }

    private static string Core =>
        Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.Core");

    /// <summary>
    /// Every Win32 call Core declares, by the name Windows knows it under, with the file that
    /// declares it. The entry point where one is named, because that is what is called, and the
    /// method's own name otherwise.
    /// </summary>
    private static Dictionary<string, string> Imports()
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Core, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Import().Matches(File.ReadAllText(file)))
            {
                var entryPoint = match.Groups["entry"].Value;
                var name = entryPoint.Length > 0 ? entryPoint : match.Groups["method"].Value;

                imports[name] = Path.GetRelativePath(Core, file);
            }
        }

        Assert.True(imports.Count > 20, $"Only {imports.Count} imports were found, so the sweep is not reading Core.");

        return imports;
    }

    [GeneratedRegex(
        """\[(?:LibraryImport|DllImport)\("[^"]+"(?:[^\]]*?EntryPoint\s*=\s*"(?<entry>[^"]+)")?[^\]]*\)\](?:\s*\[[^\]]*\])*\s*(?:public|internal|private|protected)?\s*(?:static\s+)?(?:extern\s+|partial\s+)*[^\s(]+\s+(?<method>\w+)\s*\(""",
        RegexOptions.Singleline)]
    private static partial Regex Import();

    [GeneratedRegex(@"\bPostMessage\(\s*\w+\s*,\s*(?<message>\w+)")]
    private static partial Regex PostCall();
}
