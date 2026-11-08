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
    /// <para>Set on this process, which every tool it starts inherits. The one other test class that
    /// starts a real git, <see cref="ToolOutputEncodingTests"/>, runs alone, and xUnit runs the tests of
    /// one class one at a time.</para>
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

    private Task<TrackedFileFindings> CheckAsync(IReadOnlyList<string> candidates) =>
        new TrackedFileCheck(new FakeUserEnvironment(_temp.Path).WithExecutable("git", Git), ProcessRunner.Default)
            .FindTrackedAsync(candidates);

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

    private async Task GitAsync(string arguments)
    {
        var outcome = await ProcessRunner.Default
            .RunAsync(Git, $"{Isolated} -C \"{_repository}\" {arguments}", ToolRun.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, $"git {arguments} failed: {outcome.StandardError}");
    }
}
