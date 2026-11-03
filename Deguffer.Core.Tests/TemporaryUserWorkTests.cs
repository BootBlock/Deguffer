using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Work a program keeps in a temporary folder until it is saved, which the temporary files row must
/// never offer: not on its age, not with the age limit at zero, and not when it appears between the
/// preview and the clean.
///
/// <para>Every test sets the age limit to zero, so nothing but the rule under test stands between the
/// sketch and the clean.</para>
/// </summary>
public sealed class TemporaryUserWorkTests : IDisposable
{
    /// <summary>A folder name in the shape Arduino IDE 2 writes, taken from its own source.</summary>
    private const string UnsavedSketch = ".arduinoIDE-unsaved2022615-21100-iahybb.yyvh";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TemporaryUserWorkTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempDirectoryProvider CreateProvider() =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 }),
            volumes: new FakeVolumeInventory());

    private string Abandoned(int bytes, params string[] segments) =>
        TempDirectory.Age(_temp.CreateFile(bytes, segments), TimeSpan.FromDays(30));

    /// <summary>
    /// A month-old unsaved sketch beside a month-old build folder the IDE's compiler left. The sketch
    /// is neither taken nor counted, and §5.6 asserts it survived. The build folder is ordinary
    /// scratch, so the row still takes it: the rule recognises the sketch, not anything named for
    /// Arduino.
    /// </summary>
    [Fact]
    public async Task NeverOffersAnUnsavedArduinoSketchHoweverOldItIs()
    {
        var sketch = Path.Combine(UserTemp, UnsavedSketch);
        var code = Abandoned(4096, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");
        var build = Abandoned(1024, "temp", "arduino-sketch-0123456789ABCDEF", "sketch_jul15a.ino.elf");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([sketch], step.UserWork);
        Assert.Empty(step.Spared);
        Assert.Equal(1024, step.EstimatedBytes);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sketch, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present && p.HeldContentBefore);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Information && n.Message.Contains("Arduino IDE sketch", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(code), "an unsaved sketch was removed");
        Assert.False(File.Exists(build), "ordinary scratch beside the sketch was kept");
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Equal(1, result.UserWorkCount);
        Assert.Equal(0, result.SparedCount);
        Assert.Contains("never saved", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6 bites on a sketch: a clean that took it anyway is a failure, not a pass.
    /// </summary>
    [Fact]
    public async Task ReportsAFailureWhenTheSketchIsGone()
    {
        var sketch = Path.Combine(UserTemp, UnsavedSketch);
        Abandoned(4096, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(sketch, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(sketch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saving a temporary sketch in the IDE deletes its folder, so a user who saves it after reading
    /// the plan and then presses Clean leaves the plan keeping a folder that has gone. The clean did not
    /// take it, so §5.6 reports it removed from outside rather than as a lost sketch, and still holds
    /// the run to everything else: the scratch beside it goes and the temporary folder stays.
    /// </summary>
    [Fact]
    public async Task ASketchSavedBetweenThePreviewAndTheCleanIsReportedAsRemovedFromOutside()
    {
        var sketch = Path.Combine(UserTemp, UnsavedSketch);
        Abandoned(4096, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");
        var build = Abandoned(1024, "temp", "arduino-sketch-0123456789ABCDEF", "sketch_jul15a.ino.elf");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(sketch, Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp).UserWork);

        Directory.Delete(sketch, recursive: true);

        var result = await provider.ExecuteAsync(plan);
        var verification = result.Verification!;

        Assert.False(File.Exists(build), "ordinary scratch beside the saved sketch was kept");
        Assert.True(Directory.Exists(UserTemp), "the temporary folder itself was removed");
        Assert.Empty(verification.Failures);

        var check = Assert.Single(verification.RemovedFromOutside);
        Assert.Equal(sketch, check.Subject, ignoreCase: true);
        Assert.Contains("already gone when this clean began", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sketch begun while the preview was on screen is not in the plan, and with no age limit
    /// nothing else holds it back. The clean looks again and keeps it.
    /// </summary>
    [Fact]
    public async Task KeepsASketchBegunAfterThePreview()
    {
        Abandoned(1024, "temp", "abandoned.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp).UserWork);

        var code = _temp.CreateFile(4096, "temp", ".arduinoIDE-unsaved20261102-4242-abcdef.ghij", "sketch_nov2a", "sketch_nov2a.ino");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(code), "a sketch begun after the preview was removed");
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Equal(1, result.UserWorkCount);
        Assert.Equal(0, result.SparedCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§7.1: Explore refuses the sketch and everything in it, and not the folder around it.</summary>
    [Fact]
    public async Task ExploreRefusesAnUnsavedSketch()
    {
        _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp"));

        var sketch = Path.Combine(UserTemp, UnsavedSketch);
        var idle = Path.Combine(UserTemp, "abandoned");
        Abandoned(4096, "profile", "AppData", "Local", "Temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");
        Abandoned(1024, "profile", "AppData", "Local", "Temp", "abandoned", "old.tmp");

        var provider = CreateProvider();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.False(policy.MayRemove(sketch).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(sketch, "sketch_jul15a", "sketch_jul15a.ino")).IsAllowed);
        Assert.True(policy.MayRemove(idle).IsAllowed);
    }

    /// <summary>
    /// A folder that would not be listed may hold a sketch nobody could see, so the plan does not
    /// offer it, names it as a survivor, and Explore refuses all of it.
    /// </summary>
    [Fact]
    public async Task OffersNothingInAFolderItCouldNotSearchForUnsavedWork()
    {
        _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp"));

        var idle = Path.Combine(UserTemp, "abandoned");
        Abandoned(1024, "profile", "AppData", "Local", "Temp", "abandoned", "old.tmp");

        using var denied = new DeniedDirectory(UserTemp);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.DoesNotContain(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path.Equals(UserTemp, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(UserTemp, StringComparison.OrdinalIgnoreCase));
        Assert.False(policy.MayRemove(idle).IsAllowed);
    }

    /// <summary>
    /// A folder the clean cannot search for unsaved work is held back whole, because a sketch begun
    /// since the preview could be in it. Asked of the check itself: the removal could not list such a
    /// folder either, so a run would keep everything in it whatever the check said.
    /// </summary>
    [Fact]
    public void HoldsTheFolderBackWhenTheCleanCannotSearchIt()
    {
        Abandoned(1024, "temp", "abandoned.tmp");

        var check = new TemporaryUserWorkCheck(new LiveChildrenCheck(FakeLiveTreeInspector.NothingLive));
        var step = new ClearDirectoryStep(UserTemp, "Temporary files");

        using var denied = new DeniedDirectory(UserTemp);

        var held = Assert.Single(check.Ask(step, CancellationToken.None));
        Assert.Equal(UserTemp, held.Path);
        Assert.False(held.IsUserWork);
    }

    /// <summary>
    /// A sketch a running program is working in is kept as somebody's work and reported once, never
    /// also as an entry in use.
    /// </summary>
    [Fact]
    public async Task ReportsASketchARunningProgramIsInAsWorkAndNotAsInUse()
    {
        var sketch = Path.Combine(UserTemp, UnsavedSketch);
        Abandoned(4096, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");
        Abandoned(1024, "temp", "abandoned.tmp");

        var provider = new TempDirectoryProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: new FakeLiveTreeInspector(sketch),
            preferences: new FakePreferences(AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 }),
            volumes: new FakeVolumeInventory());

        var plan = await provider.PlanAsync();
        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([sketch], step.UserWork);
        Assert.Empty(step.Spared);

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(1, result.UserWorkCount);
        Assert.Equal(0, result.SparedCount);
        Assert.Contains("never saved", RunOutcome.For([result]).Statement, StringComparison.Ordinal);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §6.3: the folder is listed through <see cref="LongPath"/>, so the entry the rule finds comes
    /// back in display form whatever form the folder was asked in.
    /// </summary>
    [Fact]
    public void FindsTheSketchWhenTheFolderIsNamedInExtendedForm()
    {
        _temp.CreateFile(16, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");

        var scan = TemporaryUserWork.In(LongPath.Extended(UserTemp));

        var entry = Assert.Single(scan.Entries);
        Assert.False(scan.Unreadable);
        Assert.Equal(Path.Combine(UserTemp, UnsavedSketch), entry.Path);
        Assert.False(entry.Path.StartsWith(@"\\?\", StringComparison.Ordinal));
    }
}
