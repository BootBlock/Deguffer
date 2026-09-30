using System.Text;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// The backups §7.3 takes with <c>reg.exe</c>. The round trip runs the real tool against a scratch
/// key under <c>HKEY_CURRENT_USER</c>, because the arguments are only right if Windows' own tool
/// accepts them; a fake would be asserting its own reply.
/// </summary>
public sealed class RegistryBackupsTests : IDisposable
{
    private static readonly string RegExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "reg.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Folder => Path.Combine(_temp.Path, "backups");

    [Fact]
    public async Task AKeyExportedAndDeletedComesBackOnImport()
    {
        using var scratch = new ScratchKey();
        using (var entry = scratch.Key.CreateSubKey("Tool"))
        {
            entry.SetValue("DisplayName", "Tool \"Pro\" \\ Edition");
            entry.SetValue("EstimatedSize", 42, RegistryValueKind.DWord);
            entry.CreateSubKey("Child").Dispose();
        }

        var backups = new RegistryBackups(ProcessRunner.Default, Folder, RegExe, TimeProvider.System);
        var keyPath = $@"HKEY_CURRENT_USER\{scratch.Path}\Tool";

        var exported = await backups.ExportAsync(keyPath, "Tool", CancellationToken.None);
        Assert.True(exported.Succeeded, exported.Message);

        var file = Assert.Single(backups.List());
        Assert.Equal(keyPath, file.KeyPath);
        Assert.Equal("Tool \"Pro\" \\ Edition", file.DisplayName);

        scratch.Key.DeleteSubKeyTree("Tool");
        var imported = await backups.ImportAsync(file, CancellationToken.None);
        Assert.True(imported.Succeeded, imported.Message);

        using var restored = scratch.Key.OpenSubKey("Tool");
        Assert.NotNull(restored);
        Assert.Equal(42, restored.GetValue("EstimatedSize"));
        Assert.Equal(["Child"], restored.GetSubKeyNames());
    }

    [Fact]
    public async Task AMissingKeyIsAFailedBackupWithNoFile()
    {
        var backups = new RegistryBackups(ProcessRunner.Default, Folder, RegExe, TimeProvider.System);

        var exported = await backups.ExportAsync(@"HKEY_CURRENT_USER\Software\Deguffer.Tests.Missing\Nothing", "Nothing", CancellationToken.None);

        Assert.False(exported.Succeeded);
        Assert.Empty(backups.List());
    }

    [Fact]
    public async Task AKeyNameWithAQuotationMarkIsRefusedWithoutRunningAnything()
    {
        var runner = new FakeProcessRunner();
        var backups = new RegistryBackups(runner, Folder, RegExe, TimeProvider.System);

        var exported = await backups.ExportAsync(new UninstallKey(UninstallScope.CurrentUser, "Odd\"Name"), CancellationToken.None);

        Assert.False(exported.Succeeded);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task TwoBackupsInOneSecondGetTwoFiles()
    {
        var runner = new FakeProcessRunner();
        var backups = new RegistryBackups(runner, Folder, RegExe, new ManualTimeProvider());
        runner.Replying(arguments =>
        {
            File.WriteAllText(arguments.Split('"')[3], "x", Encoding.Unicode);
            return null;
        });

        var key = new UninstallKey(UninstallScope.CurrentUser, "Tool");
        var first = await backups.ExportAsync(key, CancellationToken.None);
        var second = await backups.ExportAsync(key, CancellationToken.None);

        Assert.NotEqual(first.Path, second.Path);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool", UninstallScope.Machine64)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool", UninstallScope.Machine32)]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool", UninstallScope.CurrentUser)]
    public void ABackupNamesTheEntryItRestores(string keyPath, UninstallScope scope)
    {
        var file = new RegistryBackupFile("x.reg", keyPath, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(new UninstallKey(scope, "Tool"), file.Key);
    }

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool\Child")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Other\Tool")]
    public void AFileThatNamesNoSingleEntryRestoresNone(string keyPath)
    {
        Assert.Null(new RegistryBackupFile("x.reg", keyPath, null, DateTimeOffset.UnixEpoch).Key);
    }
}
