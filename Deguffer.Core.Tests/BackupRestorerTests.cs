using System.Text;
using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Restoring a backup (§7.3): never over an entry that is there, never a file that writes beyond its
/// entry or deletes anything, never a file changed since it was listed, and checked afterwards by
/// reading the entry back.
/// </summary>
public sealed class BackupRestorerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    private string Folder => _temp.CreateDirectory("backups");

    private BackupRestorer Restorer => new(_registry, new RegistryBackups(_runner, Folder, "reg.exe", TimeProvider.System));

    private static string KeyOf(UninstallScope scope) => new UninstallKey(scope, "Tool").PhysicalPath;

    private static string Export(string key, params string[] more) => string.Join("\r\n",
        ["Windows Registry Editor Version 5.00", "", $"[{key}]", "\"DisplayName\"=\"Tool\"", "", .. more, ""]);

    private RegistryBackupFile Backup(string text, string name = "tool.reg")
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, text, Encoding.Unicode);
        return RegistryBackups.Describe(path)!;
    }

    [Fact]
    public async Task ABackupOfAnAbsentEntryIsImportedFromAPrivateCopyAndCheckedBack()
    {
        var backup = Backup(Export(KeyOf(UninstallScope.CurrentUser)));
        string? imported = null;
        _runner.Replying(arguments =>
        {
            imported = arguments.Split('"')[1];
            Assert.Equal(File.ReadAllBytes(backup.Path), File.ReadAllBytes(imported));
            _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));
            return null;
        });

        var outcome = await Restorer.RestoreAsync(backup, isElevated: false, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.NotEqual(backup.Path, imported);
        Assert.EndsWith(" /reg:64", Assert.Single(_runner.Invocations).Arguments, StringComparison.Ordinal);
        Assert.False(File.Exists(imported));
    }

    [Fact]
    public async Task AnImportThatLeavesNoEntryIsAFailedRestore()
    {
        var outcome = await Restorer.RestoreAsync(Backup(Export(KeyOf(UninstallScope.CurrentUser))), isElevated: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
    }

    /// <summary>An import applies every section, so a file that writes beyond its entry is not a backup.</summary>
    [Theory]
    [InlineData(@"[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run]")]
    [InlineData(@"[-HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall]")]
    [InlineData("\"DisplayVersion\"=-")]
    public async Task AFileThatWritesBeyondItsEntryOrDeletesIsNeverImported(string extra)
    {
        var backup = Backup(Export(KeyOf(UninstallScope.CurrentUser), extra));

        var outcome = await Restorer.RestoreAsync(backup, isElevated: true, CancellationToken.None);

        Assert.False(backup.IsConfined);
        Assert.False(Restorer.MayRestore(backup, isElevated: true).IsAllowed);
        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>The folder is writable by anything running as the user, so what is imported is what was checked.</summary>
    [Fact]
    public async Task AFileChangedAfterItWasListedIsNeverImported()
    {
        var key = KeyOf(UninstallScope.CurrentUser);
        var backup = Backup(Export(key));
        File.WriteAllText(backup.Path, Export(key, @"[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run]"), Encoding.Unicode);

        var outcome = await Restorer.RestoreAsync(backup, isElevated: true, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>An import merges, and a merge into a live entry is not a restore.</summary>
    [Fact]
    public async Task AnEntryThatIsThereIsNotImportedOver()
    {
        _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));

        var outcome = await Restorer.RestoreAsync(Backup(Export(KeyOf(UninstallScope.CurrentUser))), isElevated: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public void AnEntryWindowsWillNotDescribeIsNotImportedOver()
    {
        _registry.Refusing(UninstallScope.CurrentUser);

        Assert.False(Restorer.MayRestore(Backup(Export(KeyOf(UninstallScope.CurrentUser))), isElevated: false).IsAllowed);
    }

    [Fact]
    public void AMachineWideBackupNeedsElevation()
    {
        var backup = Backup(Export(KeyOf(UninstallScope.Machine32)));

        Assert.False(Restorer.MayRestore(backup, isElevated: false).IsAllowed);
        Assert.True(Restorer.MayRestore(backup, isElevated: true).IsAllowed);
    }

    [Fact]
    public void AFileThatNamesNoEntryIsNeverImported()
    {
        Assert.False(Restorer.MayRestore(Backup(Export(@"HKEY_CURRENT_USER\Software\Other")), isElevated: true).IsAllowed);
    }
}
