using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// A finished plan with what Windows still refuses taken out of it. The provider-level tests drive
/// this end to end; these hold the two decisions that only show at this level — what the note names,
/// and the one kind of step that is never asked about.
/// </summary>
public sealed class RecordedRefusalsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RecordedRefusalsTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RefusalRecord Record => RefusalRecord.For(_environment);

    /// <summary>
    /// The note names the places holding the most refused space first. Driving the real window showed
    /// alphabetical order naming whichever places sorted first, which told the reader nothing about
    /// where the refused space was.
    /// </summary>
    [Fact]
    public void NamesThePlacesHoldingTheMostRefusedSpaceFirst()
    {
        var step = _temp.CreateDirectory("temp");
        var sizes = new Dictionary<string, int>
        {
            ["aaa-small"] = 64,
            ["bbb-large"] = 8192,
            ["ccc-medium"] = 1024,
            ["ddd-tiny"] = 16,
        };

        var refused = new Dictionary<string, RefusalReason>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, size) in sizes)
        {
            refused[_temp.CreateFile(size, "temp", name, "Cookies")] = RefusalReason.Denied;
        }

        Record.Replace(step, [.. sizes.Keys.Select(name => Path.Combine(step, name))]);

        var plan = Plan(new ClearDirectoryStep(step, "Scratch files") { Estimated = ScanSize.FromLengths(20_000) });
        var applied = RecordedRefusals.Apply(plan, Record, new RefusingFileSystem(WindowsFileSystem.Default, refused), default);

        var note = Assert.Single(applied.Notes, n => n.Severity == PlanNoteSeverity.Warning);

        Assert.Contains("in 'bbb-large', 'ccc-medium', 'aaa-small' and 1 more", note.Message, StringComparison.Ordinal);
        Assert.Equal(20_000 - sizes.Values.Sum(), applied.EstimatedBytes);
    }

    /// <summary>
    /// A Recycle Bin is emptied by the shell, not by Deguffer's removal, so a record naming one —
    /// which nothing writes — is not a reason to open anything inside it for deletion.
    /// </summary>
    [Fact]
    public void NeverAsksAboutARecycleBinTheShellEmpties()
    {
        var bin = _temp.CreateDirectory("volume", "$Recycle.Bin", "S-1-5-21-1111111111-2222222222-3333333333-1001");
        var inside = _temp.CreateFile(4096, "volume", "$Recycle.Bin", "S-1-5-21-1111111111-2222222222-3333333333-1001", "$RABCDEF.bin");

        Record.Replace(bin, [inside]);

        var recorder = new RecordingFileSystem(new RefusingFileSystem(
            WindowsFileSystem.Default, new Dictionary<string, RefusalReason> { [inside] = RefusalReason.Denied }));

        var plan = Plan(new EmptyRecycleBinStep(bin, "A Recycle Bin") { Estimated = ScanSize.FromLengths(4096) });

        Assert.Same(plan, RecordedRefusals.Apply(plan, Record, recorder, default));
        Assert.Empty(recorder.Probed);
    }

    /// <summary>
    /// A leftover that frees no bytes is chosen on its entries, so what Windows still refuses comes
    /// out of the count as well as the size — and a refused file keeps every folder above it standing
    /// too. Left in, the count would offer folders the removal cannot take.
    /// </summary>
    [Fact]
    public void TakesWhatWindowsStillRefusesOutOfTheCountWithEveryFolderAboveIt()
    {
        var leftover = _temp.CreateDirectory("leftover");
        var held = _temp.CreateFile(64, "leftover", "tool-results", "held.txt");
        _temp.CreateFile(32, "leftover", "tool-results", "free.txt");

        Record.Replace(leftover, [Path.Combine(leftover, "tool-results")]);

        var plan = Plan(new DeleteDirectoryStep(leftover, "A leftover")
        {
            Estimated = new ScanSize(96, 96, Entries: 4),
            IsLeftover = true,
        });

        var applied = RecordedRefusals.Apply(
            plan,
            Record,
            new RefusingFileSystem(WindowsFileSystem.Default, new Dictionary<string, RefusalReason> { [held] = RefusalReason.InUse }),
            default);

        var step = Assert.Single(applied.Steps);

        // Taken: free.txt. Left: held.txt, and tool-results and leftover above it.
        Assert.Equal(1, step.Estimated.Entries);
        Assert.Equal(32, step.EstimatedBytes);
    }

    [Fact]
    public void ALeftoverWhoseEveryFileIsRefusedRemovesNothing()
    {
        var leftover = _temp.CreateDirectory("leftover");
        var held = _temp.CreateFile(64, "leftover", "tool-results", "held.txt");

        Record.Replace(leftover, [Path.Combine(leftover, "tool-results")]);

        var plan = Plan(new DeleteDirectoryStep(leftover, "A leftover")
        {
            Estimated = new ScanSize(64, 64, Entries: 3),
            IsLeftover = true,
        });

        var applied = RecordedRefusals.Apply(
            plan,
            Record,
            new RefusingFileSystem(WindowsFileSystem.Default, new Dictionary<string, RefusalReason> { [held] = RefusalReason.Denied }),
            default);

        var step = Assert.Single(applied.Steps);

        Assert.Equal(0, step.Estimated.Entries);
        Assert.False(step.RemovesSomething, "a leftover Windows will not let go of was offered as removable");
    }

    /// <summary>
    /// A folder cleared in place stays whatever is refused, and its own entry was never in the count,
    /// so only what is inside it comes out.
    /// </summary>
    [Fact]
    public void TakesOnlyWhatIsInsideAFolderClearedInPlaceOutOfTheCount()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var held = _temp.CreateFile(64, "scratch", "session", "held.txt");
        _temp.CreateFile(32, "scratch", "session", "free.txt");

        Record.Replace(scratch, [Path.Combine(scratch, "session")]);

        var plan = Plan(new ClearDirectoryStep(scratch, "Scratch files") { Estimated = new ScanSize(96, 96, Entries: 3) });

        var applied = RecordedRefusals.Apply(
            plan,
            Record,
            new RefusingFileSystem(WindowsFileSystem.Default, new Dictionary<string, RefusalReason> { [held] = RefusalReason.InUse }),
            default);

        // Taken: free.txt. Left: held.txt and session. The scratch folder itself was never counted.
        Assert.Equal(1, Assert.Single(applied.Steps).Estimated.Entries);
    }

    /// <summary>
    /// A recorded place Windows would not describe is said, not passed over. Silence reads as
    /// "nothing is refused any more", and the row then promises back everything it measured. Nothing
    /// was measured there, so nothing comes out of the size, and the note says the size may be high.
    /// </summary>
    [Fact]
    public void SaysWhichRecordedPlaceCouldNotBeAskedAboutAndLeavesTheSizeAsMeasured()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        _temp.CreateFile(4096, "temp", "profile", "Cookies");

        Record.Replace(step, [profile]);

        var plan = Plan(new ClearDirectoryStep(step, "Scratch files") { Estimated = ScanSize.FromLengths(20_000) });
        var applied = RecordedRefusals.Apply(
            plan, Record, new UndescribableDirectoryFileSystem(WindowsFileSystem.Default, profile), default);

        var note = Assert.Single(applied.Notes, n => n.Severity == PlanNoteSeverity.Warning);

        Assert.Contains("Windows would not say what is at 'profile'", note.Message, StringComparison.Ordinal);
        Assert.Contains("could not check", note.Message, StringComparison.Ordinal);
        Assert.Equal(20_000, applied.EstimatedBytes);
        Assert.False(applied.HasRefusedContent, "a place nobody could ask about was reported as still refused");
    }

    private static CleanupPlan Plan(CleanupStep step) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = [step],
    };
}
