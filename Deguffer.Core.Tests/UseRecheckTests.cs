using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3 asked again at the clean. A plan decides at Preview that nothing is using what it offers, and a
/// preview can sit on screen while an editor opens, a build starts or a session resumes. The run asks
/// each step's <see cref="DeleteStep.UseCheck"/> immediately before the step, and these tests hold it to
/// what it does with the answer. What each provider asks is tested with that provider.
/// </summary>
public sealed class UseRecheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The case the check exists for. The preview found nothing using the directory, and something
    /// took it up before the clean. It stays, the reason is the check's, and §5.6 proves it standing.
    /// </summary>
    [Fact]
    public async Task ADirectorySomethingStartedUsingAfterThePreviewIsNotRemoved()
    {
        var obj = _temp.CreateDirectory("App", "obj");
        _temp.CreateFile(4096, "App", "obj", "project.assets.json");

        var check = new FakeUseCheck(step => [new InUseNow(step.Path, "devenv is working in App")]);
        var result = await Execute(new DeleteDirectoryStep(obj, "Build output") { UseCheck = check });
        var outcome = Assert.Single(result.Steps);

        Assert.True(Directory.Exists(obj), "a directory something is using now was removed");
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Equal("Nothing was removed: devenv is working in App.", outcome.Message);
        Assert.Equal(1, check.Asked);
        AssertProvedStanding(result, obj);
    }

    /// <summary>A check that finds nothing in use changes nothing about the step.</summary>
    [Fact]
    public async Task ADirectoryNothingIsUsingIsRemovedAsPlanned()
    {
        var obj = _temp.CreateDirectory("App", "obj");
        _temp.CreateFile(4096, "App", "obj", "project.assets.json");

        var check = new FakeUseCheck(_ => []);
        var result = await Execute(new DeleteDirectoryStep(obj, "Build output") { UseCheck = check });

        Assert.False(Directory.Exists(obj), "a directory nothing is using was left");
        Assert.True(Assert.Single(result.Steps).Succeeded);
        Assert.Equal(1, check.Asked);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task AFileSomethingStartedUsingAfterThePreviewIsNotRemoved()
    {
        var file = _temp.CreateFile(512, "ide", "12345.lock");

        var result = await Execute(new DeleteFileStep(file, "A handshake file")
        {
            UseCheck = new FakeUseCheck(step => [new InUseNow(step.Path, "the editor that wrote it is running again")]),
        });

        Assert.True(File.Exists(file), "a file something is using now was removed");
        Assert.Equal("Nothing was removed: the editor that wrote it is running again.", Assert.Single(result.Steps).Message);
        AssertProvedStanding(result, file);
    }

    /// <summary>
    /// A folder cleared in place does its job around an entry something took up after the preview, as
    /// it does around the entries the preview found. The check named a place deep inside the entry,
    /// and the removal can only spare an entry by its own path, so the whole entry is spared. Sparing
    /// the deep place alone would have the walk go into the entry and take everything around it.
    /// </summary>
    [Fact]
    public async Task AClearSparesTheWholeEntrySomethingTookUpAndClearsTheRest()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        var session = _temp.CreateDirectory("scratch", "live", "session");
        var abandoned = _temp.CreateFile(4096, "scratch", "abandoned.tmp");
        var beside = _temp.CreateFile(2048, "scratch", "live", "beside.txt");
        _temp.CreateFile(1024, "scratch", "live", "session", "state.bin");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files")
        {
            UseCheck = new FakeUseCheck(_ => [new InUseNow(session, "tool.exe is working in it")]),
        });

        var outcome = Assert.Single(result.Steps);

        Assert.False(File.Exists(abandoned), "the clear held back what nothing was using");
        Assert.True(File.Exists(beside), "the walk went into the entry something was using");
        Assert.True(outcome.Succeeded);
        Assert.Equal(4096, outcome.BytesReclaimed);
        Assert.Equal(1, outcome.Spared);
        AssertProvedStanding(result, live);
    }

    /// <summary>
    /// An entry the plan already spared is not asserted twice. The plan protects it, and a second
    /// assertion would print the same folder twice in the report.
    /// </summary>
    [Fact]
    public async Task AnEntryThePlanAlreadySparedIsNotAddedAgain()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        _temp.CreateFile(1024, "scratch", "live", "state.bin");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files")
        {
            Spared = [live],
            UseCheck = new FakeUseCheck(_ => [new InUseNow(live, "tool.exe is working in it")]),
        });

        Assert.True(Directory.Exists(live));
        Assert.Empty(result.Verification!.Checks);
    }

    /// <summary>
    /// A check that names the folder itself, or somewhere outside it, cannot be answered by sparing an
    /// entry. The whole step is held back, which is the direction a check answering about the wrong
    /// place has to fail in.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClearIsHeldBackWholeWhereTheCheckNamesNoEntryInside(bool namesTheFolder)
    {
        var scratch = _temp.CreateDirectory("scratch");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        var abandoned = _temp.CreateFile(4096, "scratch", "abandoned.tmp");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files")
        {
            UseCheck = new FakeUseCheck(_ => [new InUseNow(namesTheFolder ? scratch : elsewhere, "tool.exe is running from it")]),
        });

        Assert.True(File.Exists(abandoned), "a clear the check held back went ahead");
        Assert.Equal("Nothing was removed: tool.exe is running from it.", Assert.Single(result.Steps).Message);
        AssertProvedStanding(result, scratch);
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    private Task<CleanupResult> Execute(CleanupStep step) =>
        new PlanExecutor(
                new FakeProcessRunner(),
                ParallelEnumerationScanner.Default,
                RefusalRecord.For(new FakeUserEnvironment(_temp.Path)))
            .ExecuteAsync(
                new CleanupPlan
                {
                    ProviderId = "test",
                    ProviderName = "Test",
                    Tier = SafetyTier.RegenerableCache,
                    WhatHappensOnNextUse = "Nothing.",
                    Steps = [step],
                },
                runReach: null,
                residue: null,
                progress: null,
                ct: CancellationToken.None);

    private sealed class FakeUseCheck(Func<DeleteStep, IReadOnlyList<InUseNow>> answer) : IUseCheck
    {
        public int Asked { get; private set; }

        public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
        {
            Asked++;
            return answer(step);
        }
    }
}
