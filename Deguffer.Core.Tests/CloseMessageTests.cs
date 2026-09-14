using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1: "There is one verb here, and it does not grow a second."
///
/// <para>These read Deguffer's own source rather than call anything, because what they hold is a
/// property of the whole program rather than of one code path: no test can drive the message
/// Deguffer <em>does not</em> send. A new import is how a second verb would arrive — a
/// <c>WM_QUERYENDSESSION</c> here, a <c>TerminateProcess</c> there, each reasonable on its own — and
/// this fails the moment one is declared, in either project.</para>
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
        var declared = Imports();

        Assert.Equal(
            Path.Combine("Deguffer.Core", "Memory", "WindowCalls.cs"),
            Assert.Single(declared[TheOnlyPost]));

        var source = File.ReadAllText(Path.Combine(Core, "Memory", "WindowCalls.cs"));

        // WM_CLOSE is 0x0010, and it is the only message named here.
        Assert.Contains("private const uint WindowClose = 0x0010;", source, StringComparison.Ordinal);

        var post = Assert.Single(PostCall().Matches(source));

        Assert.Equal("WindowClose", post.Groups["message"].Value);
    }

    private static string Core =>
        Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.Core");

    /// <summary>
    /// Both projects, because the rule is about Deguffer rather than about one assembly, and the
    /// shell already carries Win32 calls of its own.
    /// </summary>
    private static IEnumerable<string> Sources =>
        new[] { Core, Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App") }
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Every Win32 call Deguffer declares, by the name Windows knows it under, with the file that
    /// declares it. The entry point where one is named, because that is what is called, and the
    /// method's own name otherwise.
    ///
    /// <para>Every declaration is kept, not the last of each name, so a second declaration of the
    /// one message call cannot hide behind the first.</para>
    /// </summary>
    private static ILookup<string, string> Imports()
    {
        var imports = new List<(string Name, string File)>();

        foreach (var file in Sources)
        {
            foreach (Match match in Import().Matches(File.ReadAllText(file)))
            {
                var entryPoint = match.Groups["entry"].Value;

                imports.Add((
                    entryPoint.Length > 0 ? entryPoint : match.Groups["method"].Value,
                    Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file)));
            }
        }

        // The sweep is worth nothing if it stops matching declarations, and a count that only has to
        // beat zero would not notice. Deguffer declared 60-odd when this was written.
        Assert.True(
            imports.Count > 50,
            $"Only {imports.Count} Win32 declarations were found, so the sweep is no longer reading the source.");

        return imports.ToLookup(import => import.Name, import => import.File, StringComparer.Ordinal);
    }

    [GeneratedRegex(
        """\[(?:LibraryImport|DllImport)\("[^"]+"(?:[^\]]*?EntryPoint\s*=\s*"(?<entry>[^"]+)")?[^\]]*\)\](?:\s*\[[^\]]*\])*\s*(?:public|internal|private|protected)?\s*(?:static\s+|extern\s+|partial\s+|unsafe\s+)*[^\s(]+\s+(?<method>\w+)\s*\(""",
        RegexOptions.Singleline)]
    private static partial Regex Import();

    [GeneratedRegex(@"\bPostMessage\(\s*\w+\s*,\s*(?<message>\w+)")]
    private static partial Regex PostCall();
}
