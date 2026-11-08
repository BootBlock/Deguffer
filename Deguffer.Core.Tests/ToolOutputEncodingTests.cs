using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a real tool's output is read where Deguffer has a console set to an OEM code page, as a console
/// host such as the benchmark can, rather than the app, which has no console.
///
/// <para>The code page belongs to the console, which every test in this process shares, so these run
/// in a collection xUnit runs alone, and each puts the page back.</para>
/// </summary>
[Collection(ConsoleCodePageCollection.Name)]
public sealed class ToolOutputEncodingTests : IDisposable
{
    /// <summary>The OEM code page of Western European consoles, in which UTF-8's non-ASCII bytes read as box drawing.</summary>
    private const uint Oem850 = 850;

    /// <summary>Text whose UTF-8 bytes read differently in <see cref="Oem850"/>, one letter of it outside that page altogether.</summary>
    private const string NonAscii = "Übung café 日本";

    private static readonly string Cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static readonly string Git = UserEnvironment.Current.FindExecutable("git")
        ?? throw new InvalidOperationException("This test runs a real git, and none was found on PATH.");

    /// <summary>
    /// Settings that keep the machine's own git configuration out of the scratch repository: an
    /// identity to commit with, no signing, and no line-ending conversion.
    /// </summary>
    private const string Isolated =
        "-c user.name=test -c user.email=test@example.com -c commit.gpgsign=false -c core.autocrlf=false";

    private readonly TempDirectory _temp = new();
    private readonly ConsoleCodePage _console = ConsoleCodePage.Set(Oem850);
    private readonly ProcessRunner _runner;

    public ToolOutputEncodingTests() => _runner = new ProcessRunner(Path.Combine(_temp.Path, "tools"));

    public void Dispose()
    {
        _console.Dispose();
        _temp.Dispose();
    }

    /// <summary>A tool that writes UTF-8, run as one, is read whole whatever the console says.</summary>
    [Fact]
    public async Task AToolRunAsWritingUtf8IsReadWholeUnderAnOemConsole()
    {
        var outcome = await TypeAsync(ToolRun.Query.WritingIn(ToolOutputEncoding.Utf8));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(NonAscii + Environment.NewLine, outcome.StandardOutput);
    }

    /// <summary>
    /// Every other tool is still read in the console's code page, which every other parser was written
    /// against. This is also what shows the console really was set to <see cref="Oem850"/>.
    /// </summary>
    [Fact]
    public async Task AnyOtherToolIsStillReadInTheConsolesCodePage()
    {
        var oem = CodePagesEncodingProvider.Instance.GetEncoding((int)Oem850)!;

        var outcome = await TypeAsync(ToolRun.Query);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(oem.GetString(Encoding.UTF8.GetBytes(NonAscii)) + Environment.NewLine, outcome.StandardOutput);
    }

    /// <summary>
    /// The case the encoding exists for: git lists a tracked file under a non-ASCII name in UTF-8, and
    /// the check must still find its directory tracked. Read in the console's code page, the listed
    /// path matched no candidate, and the directory read as holding nothing tracked.
    /// </summary>
    [Fact]
    public async Task FindsTrackedFilesUnderANonAsciiNameUnderAnOemConsole()
    {
        var repository = _temp.CreateDirectory("repo");
        var file = Path.Combine(repository, "Übung", "obj", "committed.props");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "<Project />");

        // An empty template, so the machine's own hooks are not installed into the scratch repository.
        await GitAsync(repository, "init --quiet --template=");
        await GitAsync(repository, "add --all");
        await GitAsync(repository, "commit --quiet -m fixture");

        var candidate = Path.GetDirectoryName(file)!;

        var findings = await new TrackedFileCheck(new FakeUserEnvironment(_temp.Path).WithExecutable("git", Git), _runner)
            .FindTrackedAsync([candidate]);

        Assert.Equal([candidate], findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary><see cref="NonAscii"/> as UTF-8, written to a pipe as the bytes in the file by <c>type</c>.</summary>
    private Task<CommandOutcome> TypeAsync(ToolRun run)
    {
        var file = Path.Combine(_temp.Path, "utf8.txt");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(NonAscii));

        return _runner.RunAsync(Cmd, $"/d /c type \"{file}\"", run, CancellationToken.None);
    }

    private async Task GitAsync(string repository, string arguments)
    {
        var outcome = await _runner.RunAsync(Git, $"{Isolated} -C \"{repository}\" {arguments}", ToolRun.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, $"git {arguments} failed: {outcome.Message}");
    }

    /// <summary>
    /// This process's console set to one output code page until disposed, and then put back. A console
    /// is made where the process has none, and freed again.
    /// </summary>
    private sealed class ConsoleCodePage : IDisposable
    {
        private readonly uint _before;
        private readonly bool _made;

        private ConsoleCodePage(uint before, bool made)
        {
            _before = before;
            _made = made;
        }

        public static ConsoleCodePage Set(uint codePage)
        {
            var before = GetConsoleOutputCP();

            // GetConsoleOutputCP answers 0 where the process has no console.
            var made = before == 0;

            if (made && !AllocConsole())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (!SetConsoleOutputCP(codePage))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new ConsoleCodePage(before, made);
        }

        public void Dispose()
        {
            if (_made)
            {
                FreeConsole();
            }
            else
            {
                SetConsoleOutputCP(_before);
            }
        }

        // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks; the test project does not
        // enable it.
        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleOutputCP(uint codePage);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();
    }
}

/// <summary>
/// The tests that set this process's console code page, run after every other test and alone, because
/// a tool another test started meanwhile would be read in that page.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCodePageCollection
{
    public const string Name = "Console code page";
}
