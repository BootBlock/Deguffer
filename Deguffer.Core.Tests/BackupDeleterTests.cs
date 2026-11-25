using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Deleting backups (§7.3): only a backup directly in the backup folder, only the file the user saw
/// listed, through the extended-length form (§6.3), and followed by the §5.6 check that the folder
/// and every other file in it survived.
/// </summary>
public sealed class BackupDeleterTests : IDisposable
{
    private const string RegExe = @"C:\Windows\System32\reg.exe";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Folder => _temp.CreateDirectory("backups");

    private BackupDeleter Deleter(IFileSystem? fileSystem = null) => new(Folder, fileSystem ?? WindowsFileSystem.Default);

    private IReadOnlyList<RegistryBackupFile> Listed() =>
        new RegistryBackups(new FakeProcessRunner(), Folder, RegExe, TimeProvider.System).List(CancellationToken.None);

    /// <summary>Write a backup of <paramref name="name"/>'s entry, as reg.exe would.</summary>
    private string Write(string file, string name, string? folder = null)
    {
        var path = Path.Combine(folder ?? Folder, file);
        var key = new UninstallKey(UninstallScope.CurrentUser, name).PhysicalPath;
        File.WriteAllText(path, $"Windows Registry Editor Version 5.00\r\n\r\n[{key}]\r\n\"DisplayName\"=\"{name}\"\r\n", Encoding.Unicode);
        return path;
    }

    private RegistryBackupFile ListedAs(string path) => Listed().Single(b => b.Path == LongPath.Display(path));

    /// <summary>
    /// The chosen file goes, and the §5.6 negative holds: the folder, the backup beside it, a file that
    /// is not a backup and the copy an interrupted restore left are all still there, and each was checked.
    /// </summary>
    [Fact]
    public async Task OnlyTheChosenBackupGoesAndEverythingBesideItIsChecked()
    {
        var chosen = Write("20260101-000000 CurrentUser Tool.reg", "Tool");
        var beside = Write("20260102-000000 CurrentUser Other.reg", "Other");
        var stranger = Path.Combine(Folder, "notes.txt");
        File.WriteAllText(stranger, "kept");
        var restoring = Write(".restoring-0123.reg", "Tool");

        var report = await Deleter().DeleteAsync([ListedAs(chosen)], CancellationToken.None);

        Assert.True(report.IsComplete, report.Summary);
        Assert.False(File.Exists(chosen));
        Assert.True(Directory.Exists(Folder));
        Assert.True(File.Exists(beside));
        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(restoring));
        Assert.Equal(
            new[] { Folder, beside, restoring, stranger }.Order(StringComparer.OrdinalIgnoreCase),
            report.Verification.Checks.Where(c => c.Outcome == VerificationOutcome.Survived).Select(c => c.Subject).Order(StringComparer.OrdinalIgnoreCase));
        Assert.StartsWith("Deleted 1 backup.", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>§6.3: the path that crosses into Win32 is the extended-length form, never the display form.</summary>
    [Fact]
    public async Task TheFileIsDeletedThroughTheExtendedLengthForm()
    {
        var chosen = Write("tool.reg", "Tool");
        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);

        await Deleter(recorder).DeleteAsync([ListedAs(chosen)], CancellationToken.None);

        Assert.Contains(LongPath.Extended(chosen), recorder.Paths);
        Assert.All(recorder.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// A file outside the backup folder, or named as no backup is, is refused before anything is
    /// read, whatever the listing record says. Only the folder's own backups can be chosen, so this
    /// is the second decision, not the first.
    /// </summary>
    [Fact]
    public async Task AFileThatIsNotABackupInTheFolderIsNeverDeleted()
    {
        var elsewhere = Write("tool.reg", "Tool", _temp.CreateDirectory("elsewhere"));
        var copy = Write(".restoring-0123.reg", "Tool");
        var key = new UninstallKey(UninstallScope.CurrentUser, "Tool").PhysicalPath;
        RegistryBackupFile[] chosen =
        [
            new(LongPath.Display(elsewhere), key, "Tool", File.GetLastWriteTimeUtc(elsewhere), IsConfined: true),
            new(LongPath.Display(copy), key, "Tool", File.GetLastWriteTimeUtc(copy), IsConfined: true),
        ];

        var report = await Deleter().DeleteAsync(chosen, CancellationToken.None);

        Assert.Empty(report.Deleted);
        Assert.True(File.Exists(elsewhere));
        Assert.True(File.Exists(copy));
        Assert.All(chosen, backup => Assert.False(Deleter().MayDelete(backup).IsAllowed));
    }

    /// <summary>A file written after it was listed is not the backup the user chose, so it is left and says so.</summary>
    [Fact]
    public async Task AFileChangedSinceItWasListedIsLeft()
    {
        var chosen = Write("tool.reg", "Tool");
        var listed = ListedAs(chosen);
        File.SetLastWriteTimeUtc(chosen, listed.Written.UtcDateTime.AddMinutes(5));

        var report = await Deleter().DeleteAsync([listed], CancellationToken.None);

        Assert.True(File.Exists(chosen));
        Assert.Contains("changed after it was listed", Assert.Single(report.NotDeleted).Message, StringComparison.Ordinal);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>A chosen file that went before the deletion reached it is not counted as deleted.</summary>
    [Fact]
    public async Task AFileGoneBeforeTheDeletionIsNotCountedAsDeleted()
    {
        var chosen = Write("tool.reg", "Tool");
        var listed = ListedAs(chosen);
        var deleter = Deleter();
        File.Delete(chosen);

        var report = await deleter.DeleteAsync([listed], CancellationToken.None);

        Assert.Empty(report.Deleted);
        Assert.Contains("already gone", Assert.Single(report.NotDeleted).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6 bites: a file beside the chosen one that goes while the deletion runs fails the check and
    /// is named, and a chosen one Deguffer did not delete is named as removed from outside.
    /// </summary>
    [Fact]
    public async Task AFileBesideTheDeletedOneThatGoesFailsTheCheck()
    {
        var chosen = Write("a.reg", "A");
        var other = Write("b.reg", "B");
        var beside = Write("c.reg", "C");
        var listed = Listed();
        var changed = ListedAs(other);
        var hooked = new DeletionHookFileSystem(WindowsFileSystem.Default, LongPath.Display(chosen), () =>
        {
            File.Delete(beside);
            File.Delete(other);
        });

        var report = await Deleter(hooked).DeleteAsync([ListedAs(chosen), changed], CancellationToken.None);

        Assert.False(report.Verification.Passed);
        Assert.Equal(LongPath.Display(beside), Assert.Single(report.Verification.Failures).Subject);
        Assert.Equal(LongPath.Display(other), Assert.Single(report.Verification.RemovedFromOutside).Subject);
        Assert.Equal(3, listed.Count);
    }

    /// <summary>A file another program holds open is left, and the report says why.</summary>
    [Fact]
    public async Task AFileHeldOpenIsLeftAndSaysWhy()
    {
        var chosen = Write("tool.reg", "Tool");
        var listed = ListedAs(chosen);

        using (new FileStream(chosen, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var report = await Deleter().DeleteAsync([listed], CancellationToken.None);

            Assert.Contains("Another program has the file open", Assert.Single(report.NotDeleted).Message, StringComparison.Ordinal);
            Assert.False(report.IsComplete);
        }

        Assert.True(File.Exists(chosen));
    }

    /// <summary>
    /// The question names every file by its full path, says the loss is permanent and that the
    /// registry is not changed, and its button repeats the verb.
    /// </summary>
    [Fact]
    public void TheConfirmationNamesEveryFileAndSaysTheLossIsPermanent()
    {
        var one = ListedAs(Write("a.reg", "A"));
        var two = ListedAs(Write("b.reg", "B"));

        var single = InstalledAppsPrompt.ForBackupDeletion([one]);
        var both = InstalledAppsPrompt.ForBackupDeletion([one, two]);

        Assert.Equal("Delete the backup of 'A' for good?", single.Title);
        Assert.Equal("Delete backup", single.ConfirmLabel);
        Assert.Equal("Delete 2 backups for good?", both.Title);
        Assert.Equal("Delete backups", both.ConfirmLabel);
        Assert.Equal([one.Path, two.Path], both.Items);
        Assert.Contains("does not go to the Recycle Bin", both.Consequence, StringComparison.Ordinal);
        Assert.Contains("Nothing in the registry changes", both.Consequence, StringComparison.Ordinal);
    }
}
