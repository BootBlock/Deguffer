using Deguffer.Core.Health.Smartmontools;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// #751: finding and reading the drive database of an installed smartmontools, through the fake
/// environment and files in a temporary folder, never the machine's own installation.
/// </summary>
public sealed class InstalledDriveDatabaseTests : IDisposable
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\smartmontools";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly string _install;
    private readonly string _bin;

    public InstalledDriveDatabaseTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _install = Path.Combine(_temp.Path, "Program Files", "smartmontools");
        _bin = Path.Combine(_install, "bin");
        Directory.CreateDirectory(_bin);
    }

    public void Dispose() => _temp.Dispose();

    private InstalledDriveDatabaseTests Installed(RegistryView view = RegistryView.Registry64)
    {
        _environment.WithMachineRegistryValue(UninstallKey, "InstallLocation", _install, view);
        return this;
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_bin, name), text);

    private DatabaseReading Read() => new InstalledDriveDatabase(_environment).Read();

    [Fact]
    public void NoSmartmontoolsIsNotInstalled() => Assert.Same(DatabaseReading.NotInstalled, Read());

    [Fact]
    public void AnInstallationWithNoDatabaseFileIsNotInstalled()
    {
        Installed();

        Assert.Same(DatabaseReading.NotInstalled, Read());
    }

    [Theory]
    [InlineData(RegistryView.Registry64)]
    [InlineData(RegistryView.Registry32)]
    public void ReadsTheDatabaseFromTheFolderTheInstallerRecorded(RegistryView view)
    {
        Installed(view).Write("drivedb.h", InventedDriveDatabase.Text);

        var reading = Read();

        Assert.Equal(DatabaseState.Loaded, reading.State);
        Assert.Equal(DatabaseVerdict.Found, reading.Database!.Find(InventedDriveDatabase.Model, "1").Verdict);
    }

    [Fact]
    public void FindsSmartctlOnPathWhereNoInstallerRecordedIt()
    {
        _environment.WithExecutable("smartctl", Path.Combine(_bin, "smartctl.exe"));
        Write("drivedb.h", InventedDriveDatabase.Text);

        Assert.Equal(DatabaseState.Loaded, Read().State);
    }

    [Fact]
    public void TheUsersAdditionsAreSearchedBeforeTheDatabase()
    {
        Installed().Write("drivedb.h", InventedDriveDatabase.Text);
        Write("drivedb-add.h", """{ "Example local family", "EXAMPLE SSD .*", "", "", "-v 9,raw48,Local_Hours" },""");

        Assert.Equal("Example local family", Read().Database!.Find(InventedDriveDatabase.Model, "1").Family);
    }

    [Fact]
    public void AFileThatCannotBeReadSaysWhichAndWhere()
    {
        Installed().Write("drivedb.h", InventedDriveDatabase.Text);
        Write("drivedb-add.h", "{ \"a\", \"b\", \"c\", \"d\", \"e\" }\n\n{ broken");

        var reading = Read();

        Assert.Equal(DatabaseState.Unreadable, reading.State);
        Assert.Null(reading.Database);
        Assert.Equal("drivedb-add.h", reading.FailedFile);
        Assert.Equal(3, reading.FailedLine);
    }

    [Fact]
    public void AFileThatIsNotTextCannotBeRead()
    {
        Installed();
        File.WriteAllBytes(Path.Combine(_bin, "drivedb.h"), [0x7B, 0xFF, 0xFE, 0x7D]);

        Assert.Equal(new DatabaseReading(DatabaseState.Unreadable, null, "drivedb.h"), Read());
    }

    [Fact]
    public void AFileWindowsWillNotDescribeIsUnreadableRatherThanAbsent()
    {
        Installed().Write("drivedb.h", InventedDriveDatabase.Text);

        using (DeniedDirectory.WithUnreadableFile(Path.Combine(_bin, "drivedb.h")))
        {
            Assert.Equal(DatabaseState.Unreadable, Read().State);
        }
    }

    [Fact]
    public void AReadingIsKeptUntilAFileChanges()
    {
        Installed().Write("drivedb.h", InventedDriveDatabase.Text);
        var database = new InstalledDriveDatabase(_environment);

        var first = database.Read();
        Assert.Same(first, database.Read());

        Write("drivedb.h", InventedDriveDatabase.Text + """{ "Example appended family", "APPENDED", "", "", "" },""");
        File.SetLastWriteTimeUtc(Path.Combine(_bin, "drivedb.h"), DateTime.UtcNow.AddMinutes(1));

        var second = database.Read();
        Assert.NotSame(first, second);
        Assert.Equal("Example appended family", second.Database!.Find("APPENDED", string.Empty).Family);
    }
}
