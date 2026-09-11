using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The preview's question about a place the last clean was refused: does Windows still refuse it,
/// and how much? The answer is taken out of the step's estimate, so it has to count exactly what the
/// removal would attempt — no more, which would hide bytes the clean will take, and no less, which is
/// issue #117.
///
/// <para>Every open for deletion is also a brief cost to any other program opening that file, so
/// where the check does <em>not</em> open anything is asserted as carefully as what it counts.</para>
/// </summary>
public sealed class RefusalCheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static RefusingFileSystem Refusing(params (string Path, RefusalReason Reason)[] refused) =>
        new(WindowsFileSystem.Default, refused.ToDictionary(r => r.Path, r => r.Reason, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void CountsWhatARecordedPlaceStillRefusesWithTheSizeOfEachKind()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        var denied = _temp.CreateFile(4096, "temp", "profile", "Default", "Cookies");
        var held = _temp.CreateFile(1024, "temp", "profile", "lockfile");
        _temp.CreateFile(512, "temp", "profile", "removable.bin");

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(step, "Scratch"),
            [profile],
            MinimumAge.Off,
            Refusing((denied, RefusalReason.Denied), (held, RefusalReason.InUse)),
            default);

        Assert.Equal(new RefusalTally(1, 4096), finding.Refused.Denied);
        Assert.Equal(new RefusalTally(1, 1024), finding.Refused.InUse);
        Assert.Equal(profile, Assert.Single(finding.Places).Place);
    }

    /// <summary>
    /// A refusal that has lifted is counted again at once, without waiting for a clean the row would
    /// no longer offer — which is the whole reason the record says where to look rather than what
    /// the answer was.
    /// </summary>
    [Fact]
    public void LeavesOutAPlaceWindowsNoLongerRefuses()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        _temp.CreateFile(4096, "temp", "profile", "Default", "Cookies");

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(step, "Scratch"), [profile], MinimumAge.Off, Refusing(), default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(finding.Places);
    }

    /// <summary>
    /// Only the places the record names are asked about. A refused file somewhere else in the step is
    /// the first preview's to over-offer and the next clean's to record — opening it here would be
    /// the per-file cost on every preview that the record exists to avoid.
    /// </summary>
    [Fact]
    public void OpensNothingOutsideThePlacesTheRecordNames()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        var recorded = _temp.CreateFile(2048, "temp", "profile", "Cookies");
        var unrecorded = _temp.CreateFile(8192, "temp", "elsewhere", "Cookies");

        var recorder = new RecordingFileSystem(
            Refusing((recorded, RefusalReason.Denied), (unrecorded, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(new ClearDirectoryStep(step, "Scratch"), [profile], MinimumAge.Off, recorder, default);

        Assert.Equal(new RefusalTally(1, 2048), finding.Refused.Denied);
        Assert.DoesNotContain(recorder.Probed, p => LongPath.Display(p).Equals(unrecorded, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A file the guard protects is already out of the estimate and is not one the removal would
    /// attempt, so counting it here would take it out a second time.
    /// </summary>
    [Fact]
    public void DoesNotCountAFileTheGuardHasAlreadyLeftOut()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        var recent = _temp.CreateFile(1024, "temp", "profile", "written-today");
        var old = TempDirectory.Age(_temp.CreateFile(4096, "temp", "profile", "abandoned"), TimeSpan.FromDays(30));

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(step, "Scratch"),
            [profile],
            MinimumAge.WithinHours(8, DateTime.UtcNow),
            Refusing((recent, RefusalReason.Denied), (old, RefusalReason.Denied)),
            default);

        Assert.Equal(new RefusalTally(1, 4096), finding.Refused.Denied);
    }

    /// <summary>
    /// §5.3's live-process exclusion reaches the check too. A spared entry is already out of the
    /// estimate, and the program working in it is exactly the one an open for deletion could get in
    /// the way of.
    /// </summary>
    [Fact]
    public void NeverOpensAnythingInsideAnEntryTheStepSpares()
    {
        var step = _temp.CreateDirectory("temp");
        var live = _temp.CreateDirectory("temp", "live-session");
        var denied = _temp.CreateFile(4096, "temp", "live-session", "working.db");

        var recorder = new RecordingFileSystem(Refusing((denied, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(step, "Scratch") { Spared = [live] }, [live], MinimumAge.Off, recorder, default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(recorder.Probed);
    }

    /// <summary>
    /// The record is a file on the user's disk. A place in it outside the step is not trusted: it
    /// would be opened for deletion and then taken out of a figure it was never part of.
    /// </summary>
    [Fact]
    public void NeverOpensARecordedPlaceOutsideTheStep()
    {
        var step = _temp.CreateDirectory("temp");
        var outside = _temp.CreateDirectory("Documents");
        var denied = _temp.CreateFile(4096, "Documents", "letter.docx");

        var recorder = new RecordingFileSystem(Refusing((denied, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(new ClearDirectoryStep(step, "Scratch"), [outside], MinimumAge.Off, recorder, default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(recorder.Probed);
    }

    /// <summary>A step naming one file records that file, and it is the file that is asked about.</summary>
    [Fact]
    public void AsksAboutTheFileItselfWhenTheStepIsOneFile()
    {
        var dump = _temp.CreateFile(8192, "Windows", "MEMORY.DMP");

        var finding = RefusalCheck.Of(
            new DeleteFileStep(dump, "A crash dump"), [dump], MinimumAge.Off, Refusing((dump, RefusalReason.InUse)), default);

        Assert.Equal(new RefusalTally(1, 8192), finding.Refused.InUse);
    }

    /// <summary>
    /// §6.3, asserted on the form of the path rather than on a deep tree — see
    /// <see cref="DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/> for why
    /// only the form discriminates. The record holds display paths, so this is where the prefix has
    /// to be put back on.
    /// </summary>
    [Fact]
    public void OpensEveryFileInExtendedLengthForm()
    {
        var step = _temp.CreateDirectory("temp");
        var profile = _temp.CreateDirectory("temp", "profile");
        var denied = _temp.CreateFile(64, "temp", "profile", "Default", "Cookies");
        var dump = _temp.CreateFile(64, "temp", "dump.dmp");

        var recorder = new RecordingFileSystem(Refusing((denied, RefusalReason.Denied), (dump, RefusalReason.Denied)));

        RefusalCheck.Of(new ClearDirectoryStep(step, "Scratch"), [profile, dump], MinimumAge.Off, recorder, default);

        Assert.Equal(2, recorder.Probed.Count);
        Assert.All(recorder.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// A place that only looks inside the step is not asked about. A prefix test passes
    /// <c>&lt;step&gt;\x\..\..\..\Documents</c>, which resolves outside it — and the check would then
    /// open every file there for deletion and take their size out of a figure they were never part of.
    /// </summary>
    [Fact]
    public void NeverOpensARecordedPlaceThatOnlyLooksInsideTheStep()
    {
        var step = _temp.CreateDirectory("profile", "temp");
        var denied = _temp.CreateFile(4096, "Documents", "letter.docx");
        var disguised = Path.Combine(step, "x", "..", "..", "..", "Documents");

        Assert.True(LongPath.Contains(step, disguised), "the fixture no longer passes a prefix test");

        var recorder = new RecordingFileSystem(Refusing((denied, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(new ClearDirectoryStep(step, "Scratch"), [disguised], MinimumAge.Off, recorder, default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(recorder.Probed);
    }

    /// <summary>
    /// A place nested inside a spared entry is inside that entry, whatever the spared set matches by
    /// name. Only the step itself or an entry directly inside it is the shape the removal records, so a
    /// deeper place is not asked about at all — and a live program's files are not opened for deletion.
    /// </summary>
    [Fact]
    public void NeverOpensAPlaceNestedInsideAnEntryTheStepSpares()
    {
        var step = _temp.CreateDirectory("temp");
        var live = _temp.CreateDirectory("temp", "live-session");
        var nested = _temp.CreateDirectory("temp", "live-session", "cache");
        var denied = _temp.CreateFile(4096, "temp", "live-session", "cache", "working.db");

        var recorder = new RecordingFileSystem(Refusing((denied, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(step, "Scratch") { Spared = [live] }, [nested], MinimumAge.Off, recorder, default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(recorder.Probed);
    }

    /// <summary>
    /// A step root that has become a link is removed as a link, or left alone, and never entered. So
    /// nothing on its far side is anything the removal would attempt, and nothing there is opened.
    /// </summary>
    [Fact]
    public void OpensNothingBeneathAStepRootThatIsALink()
    {
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(4096, "elsewhere", "profile", "Cookies");

        var root = Path.Combine(_temp.Path, "temp");
        Directory.CreateSymbolicLink(root, elsewhere);

        var throughLink = Path.Combine(root, "profile", "Cookies");
        var recorder = new RecordingFileSystem(Refusing((throughLink, RefusalReason.Denied)));

        var finding = RefusalCheck.Of(
            new ClearDirectoryStep(root, "Scratch"), [Path.Combine(root, "profile")], MinimumAge.Off, recorder, default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Empty(recorder.Probed);
    }
}
