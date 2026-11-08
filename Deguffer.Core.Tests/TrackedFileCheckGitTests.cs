using System.Diagnostics;
using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7's second opinion against a real git, in a scratch repository.
///
/// The other tests answer for git with a fake, so they prove what the check asks and how it reads the
/// answer, and nothing about how git matches what it was asked. That matching is where a directory
/// the index spells in a different case from the disk read as holding nothing tracked: git answered
/// successfully, the answer was empty, and the folder was cleared. Only git can say what its
/// pathspecs match, so these ask it.
///
/// <para>Git is required rather than skipped when absent, because a test that passes when git could
/// not be found has proved nothing.</para>
/// </summary>
public sealed class TrackedFileCheckGitTests : IDisposable
{
    private static readonly string Git = UserEnvironment.Current.FindExecutable("git")
        ?? throw new InvalidOperationException("These tests run a real git, and none was found on PATH.");

    /// <summary>
    /// Settings that keep the machine's own git configuration out of the scratch repository: an
    /// identity to commit with, no signing, and no line-ending conversion.
    /// </summary>
    private const string Isolated =
        "-c user.name=test -c user.email=test@example.com -c commit.gpgsign=false -c core.autocrlf=false";

    private const string LiteralPathspecs = "GIT_LITERAL_PATHSPECS";

    private readonly TempDirectory _temp = new();
    private readonly string _repository;

    public TrackedFileCheckGitTests() => _repository = _temp.CreateDirectory("repo");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The index says <c>Proj/obj</c> and the disk says <c>Proj/Obj</c>, which a rename that changed
    /// only case leaves behind, as does a repository created on Linux. The directory still holds a
    /// tracked file, and the check must say so.
    /// </summary>
    [Fact]
    public async Task FindsTrackedFilesInADirectoryTheIndexSpellsInAnotherCase()
    {
        await CommitAsync(Path.Combine("Proj", "obj", "committed.props"));
        RenameOnDisk(Path.Combine("Proj", "obj"), Path.Combine("Proj", "Obj"));
        var candidate = Path.Combine(_repository, "Proj", "Obj");

        var findings = await CheckAsync([candidate]);

        Assert.Equal([candidate], findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// The same, where the letters that differ are outside ASCII. Git's <c>icase</c> folds ASCII
    /// letters only, so a literal pathspec for <c>Übung</c> found nothing in an index that spells it
    /// <c>übung</c>, and git answered "nothing tracked" successfully.
    /// </summary>
    [Fact]
    public async Task FindsTrackedFilesWhenTheCaseDiffersInALetterOutsideAscii()
    {
        await CommitAsync(Path.Combine("übung", "obj", "committed.props"));
        RenameOnDisk("übung", "Übung");
        var candidate = Path.Combine(_repository, "Übung", "obj");

        var findings = await CheckAsync([candidate]);

        Assert.Equal([candidate], findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// Ignoring case must not loosen what is found tracked. A name holding glob characters is still
    /// found in another case, whether it is asked about literally or, outside ASCII, by a glob in
    /// which those characters are escaped. A sibling whose name only begins with the candidate's is
    /// not inside it. A directory holding only untracked files holds nothing tracked, which is what lets the
    /// check clear ordinary intermediate output at all, and that holds where the glob asked about
    /// <c>Ärger</c> also lists the tracked files of <c>Berger</c>.
    /// </summary>
    [Fact]
    public async Task IgnoringCaseStillFindsOnlyWhatIsTrackedWithinTheCandidate()
    {
        await CommitAsync(
            Path.Combine("App[Legacy]", "obj", "committed.props"),
            Path.Combine("Lib", "objects", "committed.props"),
            Path.Combine("Berger", "obj", "committed.props"),
            Path.Combine("Ä[b]x", "obj", "committed.props"));
        Write(Path.Combine("Lib", "obj", "untracked.props"));
        Write(Path.Combine("Ärger", "obj", "untracked.props"));

        var glob = Path.Combine(_repository, "APP[legacy]", "OBJ");
        var escaped = Path.Combine(_repository, "ä[B]X", "obj");
        var prefix = Path.Combine(_repository, "Lib", "obj");
        var lookalike = Path.Combine(_repository, "Ärger", "obj");

        var findings = await CheckAsync([glob, escaped, prefix, lookalike]);

        Assert.Equal([glob, escaped], findings.Tracked.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// <c>GIT_LITERAL_PATHSPECS=1</c> in the environment Deguffer was started with puts git in global
    /// literal mode, where it does not read pathspec magic. Each pathspec was then a path beginning
    /// <c>:(</c>, matched nothing, and every candidate read as holding nothing tracked.
    ///
    /// <para>Set on this process, which every tool it starts inherits. xUnit runs the tests of one class
    /// one at a time, and the one other test class that starts a real git
    /// (<see cref="GitEnvironmentTests"/>) asks it only for a list nothing set here changes.</para>
    /// </summary>
    [Fact]
    public async Task FindsTrackedFilesWhenTheEnvironmentAsksForLiteralPathspecs()
    {
        await CommitAsync(Path.Combine("Proj", "obj", "committed.props"));
        var candidate = Path.Combine(_repository, "Proj", "obj");
        var before = Environment.GetEnvironmentVariable(LiteralPathspecs);

        Environment.SetEnvironmentVariable(LiteralPathspecs, "1");

        try
        {
            var findings = await CheckAsync([candidate]);

            Assert.Equal([candidate], findings.Tracked);
            Assert.Empty(findings.Unanswered);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiteralPathspecs, before);
        }
    }

    /// <summary>
    /// A variable in the environment Deguffer was started with that tells git which repository, work
    /// tree or index to read. git reads each over <c>-C</c>: <c>GIT_DIR</c> naming another repository,
    /// <c>GIT_WORK_TREE</c> naming the folder above the repository, which makes every pathspec begin
    /// with the repository's own name, or <c>GIT_INDEX_FILE</c> naming a file that is not there, made
    /// git list nothing and exit 0, and the candidate read as holding nothing tracked.
    ///
    /// <para>Set on this process for the reason the literal-pathspec test gives.</para>
    /// </summary>
    [Theory]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_WORK_TREE")]
    [InlineData("GIT_INDEX_FILE")]
    public async Task FindsTrackedFilesWhenTheEnvironmentNamesAnotherRepository(string variable)
    {
        await CommitAsync(Path.Combine("Proj", "obj", "committed.props"));
        var candidate = Path.Combine(_repository, "Proj", "obj");
        var elsewhere = await AnotherRepositoryAsync();
        var value = variable switch
        {
            "GIT_DIR" => Path.Combine(elsewhere, ".git"),
            "GIT_WORK_TREE" => Path.GetDirectoryName(_repository)!,
            _ => Path.Combine(elsewhere, "no-such-index"),
        };

        var findings = await CheckThroughTheRunnerWithAsync(variable, value, candidate);

        Assert.Equal([candidate], findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// The same in a linked worktree, whose <c>.git</c> is a file naming its git directory. With the
    /// variables gone, git finds that file from <c>-C</c> as it finds a <c>.git</c> folder, so the
    /// worktree's own index is the one read.
    /// </summary>
    [Fact]
    public async Task FindsTrackedFilesInALinkedWorktreeWhenTheEnvironmentNamesAnotherRepository()
    {
        await CommitAsync(Path.Combine("Proj", "obj", "committed.props"));
        var worktree = Path.Combine(_temp.Path, "linked");
        await GitAsync($"worktree add --quiet \"{worktree}\"");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "The linked worktree has no .git file.");
        var candidate = Path.Combine(worktree, "Proj", "obj");
        var elsewhere = await AnotherRepositoryAsync();

        var findings = await CheckThroughTheRunnerWithAsync("GIT_DIR", Path.Combine(elsewhere, ".git"), candidate);

        Assert.Equal([candidate], findings.Tracked);
        Assert.Empty(findings.Unanswered);
    }

    private Task<TrackedFileFindings> CheckAsync(IReadOnlyList<string> candidates) =>
        new TrackedFileCheck(new FakeUserEnvironment(_temp.Path).WithExecutable("git", Git), Utf8Runner.Instance)
            .FindTrackedAsync(candidates);

    /// <summary>
    /// Asks about <paramref name="candidate"/> through <see cref="ProcessRunner"/>, which is what
    /// starts git without the repository variables, with <paramref name="variable"/> set on this
    /// process for the length of the check.
    /// </summary>
    private async Task<TrackedFileFindings> CheckThroughTheRunnerWithAsync(string variable, string value, string candidate)
    {
        var before = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);

        try
        {
            return await new TrackedFileCheck(
                    new FakeUserEnvironment(_temp.Path).WithExecutable("git", Git),
                    new ProcessRunner(Path.Combine(_temp.Path, "tools")))
                .FindTrackedAsync([candidate]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, before);
        }
    }

    /// <summary>A second repository with one commit and nothing under <c>Proj</c>.</summary>
    private async Task<string> AnotherRepositoryAsync()
    {
        var other = _temp.CreateDirectory("other");
        File.WriteAllText(Path.Combine(other, "readme.txt"), "other");
        await GitAsync(other, "init --quiet --template=");
        await GitAsync(other, "add --all");
        await GitAsync(other, "commit --quiet -m other");
        return other;
    }

    /// <summary>Create a repository holding <paramref name="files"/>, each committed.</summary>
    private async Task CommitAsync(params string[] files)
    {
        // An empty template, so the machine's own hooks are not installed into the scratch repository.
        await GitAsync("init --quiet --template=");

        foreach (var file in files)
        {
            Write(file);
        }

        await GitAsync("add --all");
        await GitAsync("commit --quiet -m fixture");
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }

    /// <summary>
    /// Through a third name, because <see cref="Directory.Move"/> refuses a source and destination
    /// that differ only in case.
    /// </summary>
    private void RenameOnDisk(string from, string to)
    {
        var through = Path.Combine(_repository, "renaming");
        Directory.Move(Path.Combine(_repository, from), through);
        Directory.Move(through, Path.Combine(_repository, to));
    }

    private Task GitAsync(string arguments) => GitAsync(_repository, arguments);

    private static async Task GitAsync(string repository, string arguments)
    {
        var outcome = await ProcessRunner.Default
            .RunAsync(Git, $"{Isolated} -C \"{repository}\" {arguments}", ToolRun.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, $"git {arguments} failed: {outcome.StandardError}");
    }

    /// <summary>
    /// Starts the check's git with its output read as UTF-8, which is how git writes paths and how
    /// <see cref="ProcessRunner"/> reads output in the app, which has no console. In a process that
    /// has one, as the test host does, <see cref="ProcessRunner"/> reads in the console's code page,
    /// and a non-ASCII path git lists would reach the check mangled. What is under test here is what
    /// git matches, not how the runner decodes it.
    /// </summary>
    private sealed class Utf8Runner : IProcessRunner
    {
        public static readonly Utf8Runner Instance = new();

        public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            })!;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new CommandOutcome(process.ExitCode, await output, await error);
        }
    }
}
