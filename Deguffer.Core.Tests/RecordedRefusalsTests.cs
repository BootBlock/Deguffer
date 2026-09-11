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

    private static CleanupPlan Plan(CleanupStep step) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = [step],
    };
}
