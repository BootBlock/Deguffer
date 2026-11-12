using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Work a program keeps in a temporary folder until it is saved, folders and files alike, which the
/// temporary files row must never offer: not on its age, not with the age limit at zero, and not when
/// it appears between the preview and the clean.
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
    /// Krita autosaves a never-saved image into the temporary folder under the two names it offers back
    /// at start-up, the second hidden. Both are kept, counted out, named and asserted after the clean.
    /// A <c>.kra</c> autosave whose name is not Krita's never-saved pattern is ordinary scratch.
    /// </summary>
    [Fact]
    public async Task NeverOffersKritasAutosaveOfAnImageThatWasNeverSaved()
    {
        var shown = Abandoned(4096, "temp", "krita-1234-document_1-autosave.kra");
        var hidden = Abandoned(2048, "temp", ".krita-5678-document_2-autosave.kra");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var other = Abandoned(1024, "temp", "notes-autosave.kra");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([hidden, shown], step.UserWork.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1024, step.EstimatedBytes);
        Assert.All([shown, hidden], kept => Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Information && n.Message.Contains("Krita's autosave", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(shown), "Krita's autosave was removed");
        Assert.True(File.Exists(hidden), "Krita's hidden autosave was removed");
        Assert.False(File.Exists(other), "a .kra file that is not Krita's unsaved autosave was kept");
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Equal(2, result.UserWorkCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6 bites on a file as it does on a folder: a clean that took the autosave anyway fails.</summary>
    [Fact]
    public async Task ReportsAFailureWhenKritasAutosaveIsGone()
    {
        var autosave = Abandoned(4096, "temp", "krita-1234-document_1-autosave.kra");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        File.Delete(autosave);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(autosave, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An image Krita autosaved while the preview was on screen is not in the plan, and with no age
    /// limit nothing else holds it back. The clean looks again and keeps it.
    /// </summary>
    [Fact]
    public async Task KeepsAKritaAutosaveWrittenAfterThePreview()
    {
        Abandoned(1024, "temp", "abandoned.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var autosave = _temp.CreateFile(4096, "temp", "krita-4242-document_3-autosave.kra");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(autosave), "an autosave written after the preview was removed");
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Equal(1, result.UserWorkCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Blender's recovery files are kept by this row, with the row that takes Blender's session
    /// folders wired in as it is in the app. That row does not claim them, so this row neither leaves
    /// them to it nor takes them on their age: it keeps them and asserts they survived.
    /// </summary>
    [Fact]
    public async Task KeepsBlendersRecoveryFilesBesideTheRowThatTakesItsSessionFolders()
    {
        var quit = Abandoned(2048, "temp", "quit.blend");
        var autosave = Abandoned(1024, "temp", "scene_4242_autosave.blend");
        var unnamed = Abandoned(512, "temp", "4243_autosave.blend");
        Abandoned(256, "temp", "abandoned.tmp");

        var installers = new TempInstallerDownloadProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            volumes: new FakeVolumeInventory());
        var provider = new TempDirectoryProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 }),
            tenants: [installers],
            volumes: new FakeVolumeInventory());

        var plan = await provider.PlanAsync();
        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([unnamed, quit, autosave], step.UserWork.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(step.OwnedElsewhere);
        Assert.Equal(256, step.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.All([quit, autosave, unnamed], kept => Assert.True(File.Exists(kept), $"{kept} was removed"));
        Assert.Equal(3, result.UserWorkCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// NVIDIA Highlights keeps clips the player has not saved to the Gallery in one folder per game.
    /// The folder is kept whole however old its clips are, and §5.6 names it.
    /// </summary>
    [Fact]
    public async Task NeverOffersNvidiaHighlightsClipsHoweverOldTheyAre()
    {
        var highlights = Path.Combine(UserTemp, "Highlights");
        var clip = Abandoned(4096, "temp", "Highlights", "Rocket League", "clip.mp4");
        var other = Abandoned(1024, "temp", "Highlights-old", "clip.mp4");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([highlights], step.UserWork);
        Assert.Equal(1024, step.EstimatedBytes);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(highlights, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("NVIDIA Highlights", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(clip), "a Highlights clip was removed");
        Assert.False(File.Exists(other), "a folder only named like Highlights was kept");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A kind is recognised only as the entry its program writes: a folder named like Krita's
    /// autosave, or a file named like the Highlights folder, is not that work, and naming it so would
    /// be false. Names are compared without regard to case.
    /// </summary>
    [Fact]
    public void RecognisesEachKindOnlyAsTheEntryItsProgramWrites()
    {
        _temp.CreateFile(16, "temp", "krita-1-document_1-autosave.kra", "inside.txt");
        _temp.CreateFile(16, "temp", "highlights");
        var quit = _temp.CreateFile(16, "temp", "Quit.Blend");

        var scan = TemporaryUserWork.In(UserTemp);

        Assert.Equal(quit, Assert.Single(scan.Entries).Path, ignoreCase: true);
    }

    /// <summary>§7.1: Explore refuses Krita's autosave, a file, and not the scratch beside it.</summary>
    [Fact]
    public async Task ExploreRefusesKritasAutosave()
    {
        _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp"));

        var autosave = Path.Combine(UserTemp, "krita-1234-document_1-autosave.kra");
        var idle = Path.Combine(UserTemp, "abandoned.tmp");
        Abandoned(4096, "profile", "AppData", "Local", "Temp", "krita-1234-document_1-autosave.kra");
        Abandoned(1024, "profile", "AppData", "Local", "Temp", "abandoned.tmp");

        var provider = CreateProvider();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.False(policy.MayRemove(autosave).IsAllowed);
        Assert.True(policy.MayRemove(idle).IsAllowed);
    }

    /// <summary>
    /// §6.3: the folder is listed through <see cref="LongPath"/>, so each entry the rule finds, a
    /// folder or a file, comes back in display form whatever form the folder was asked in.
    /// </summary>
    [Fact]
    public void FindsTheWorkWhenTheFolderIsNamedInExtendedForm()
    {
        _temp.CreateFile(16, "temp", UnsavedSketch, "sketch_jul15a", "sketch_jul15a.ino");
        _temp.CreateFile(16, "temp", "krita-1234-document_1-autosave.kra");

        var scan = TemporaryUserWork.In(LongPath.Extended(UserTemp));

        Assert.False(scan.Unreadable);
        Assert.Equal(
            [Path.Combine(UserTemp, UnsavedSketch), Path.Combine(UserTemp, "krita-1234-document_1-autosave.kra")],
            scan.Entries.Select(entry => entry.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(scan.Entries, entry => Assert.False(entry.Path.StartsWith(@"\\?\", StringComparison.Ordinal)));
    }
}
