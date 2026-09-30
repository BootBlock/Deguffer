using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading the three <c>Uninstall</c> keys into entries (§7.3): what Windows lists, which list each
/// entry is in, and that a value of an unexpected type never drops an entry.
/// </summary>
public sealed class InstalledAppsReaderTests : IDisposable
{
    private const string ProductKey = "{12345678-9ABC-DEF0-1234-56789ABCDEF0}";

    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeWindowsInstaller _installer = new();

    public void Dispose() => _temp.Dispose();

    private InstalledAppsReading Read() => new InstalledAppsReader(_registry, _installer).Read(CancellationToken.None);

    private InstalledEntry Only() => Assert.Single(Read().Entries);

    [Fact]
    public void AnEntryWithAPresentUninstallerIsListedAndInstalled()
    {
        var uninstaller = _temp.CreateFile(1, "Tool", "unins000.exe");
        _registry.With(UninstallScope.Machine64, "Tool_is1",
            ("DisplayName", "Tool"), ("Publisher", "Example"), ("DisplayVersion", "1.2"), ("UninstallString", $"\"{uninstaller}\""));

        var entry = Only();

        Assert.Equal(("Tool", "Example", "1.2"), (entry.Name, entry.Publisher, entry.Version));
        Assert.True(entry.IsListed);
        Assert.Equal(EntryStanding.Installed, entry.Standing.Standing);
    }

    [Fact]
    public void AnEntryWhoseUninstallerAndFolderAreGoneIsStale()
    {
        var folder = Path.Combine(_temp.Path, "Gone");
        _registry.With(UninstallScope.CurrentUser, "Gone",
            ("DisplayName", "Gone"), ("UninstallString", $"\"{folder}\\unins000.exe\""), ("InstallLocation", folder));

        Assert.True(Only().IsStale);
    }

    [Fact]
    public void AnEntryWhoseFolderStandsIsNotStale()
    {
        var folder = _temp.CreateDirectory("Broken");
        _registry.With(UninstallScope.CurrentUser, "Broken",
            ("DisplayName", "Broken"), ("UninstallString", $"\"{folder}\\unins000.exe\""), ("InstallLocation", folder));

        Assert.Equal(EntryStanding.Installed, Only().Standing.Standing);
    }

    /// <summary>The earlier tool dropped an entry whose <c>EstimatedSize</c> was written as a string.</summary>
    [Fact]
    public void AValueOfAnUnexpectedTypeDoesNotDropTheEntry()
    {
        _registry.With(UninstallScope.Machine32, "Odd",
            ("DisplayName", "Odd"), ("EstimatedSize", "large"), ("SystemComponent", "1"), ("NoRemove", new byte[] { 1 }));

        var entry = Only();

        Assert.True(entry.IsListed);
        Assert.False(entry.NoRemove);
    }

    [Theory]
    [InlineData("SystemComponent", 1, EntryVisibility.SystemComponent)]
    [InlineData("ParentKeyName", "Office", EntryVisibility.Update)]
    [InlineData("ReleaseType", "Security Update", EntryVisibility.Update)]
    public void WindowsHidesWhatItsValuesTellItTo(string name, object value, EntryVisibility expected)
    {
        _registry.With(UninstallScope.Machine64, "Hidden", ("DisplayName", "Hidden"), (name, value));

        var entry = Only();

        Assert.Equal(expected, entry.Visibility);
        Assert.NotNull(EntryListing.WhyHidden(entry.Visibility));
    }

    [Fact]
    public void AnEntryWithNoNameIsHiddenAndNamedByItsKey()
    {
        _registry.With(UninstallScope.Machine64, "NoName", ("DisplayName", "  "));

        var entry = Only();

        Assert.Equal(EntryVisibility.NoName, entry.Visibility);
        Assert.Equal("NoName", entry.Name);
    }

    [Fact]
    public void AnotherAccountsProductIsHiddenAndRefused()
    {
        _installer.With(Guid.Parse(ProductKey), InstallerProductState.OtherAccount);
        _registry.With(UninstallScope.Machine64, ProductKey, ("DisplayName", "Theirs"), ("WindowsInstaller", 1));

        var entry = Only();

        Assert.Equal(EntryVisibility.OtherAccount, entry.Visibility);
        Assert.Equal(EntryStanding.OtherAccount, entry.Standing.Standing);
    }

    [Fact]
    public void AnUnreadableEntryIsReportedRatherThanDropped()
    {
        _registry.WithUnreadable(UninstallScope.Machine64, "Locked");

        var entry = Only();

        Assert.Equal(EntryVisibility.Unreadable, entry.Visibility);
        Assert.Equal(EntryStanding.Unproven, entry.Standing.Standing);
    }

    [Fact]
    public void AKeyWindowsRefusesIsReportedAsRefused()
    {
        _registry.Refusing(UninstallScope.Machine32);

        Assert.Equal([UninstallScope.Machine32], Read().RefusedScopes);
    }

    [Fact]
    public void EveryScopeIsRead()
    {
        Read();

        Assert.Equal(UninstallScopes.All.Order(), _registry.Reads.Keys.Order());
    }

    [Fact]
    public void WindowsInstallerIsAskedOncePerProductForOneReading()
    {
        _registry.With(UninstallScope.Machine64, "A", ("DisplayName", "A"), ("UninstallString", $"MsiExec.exe /X{ProductKey}"));
        _registry.With(UninstallScope.Machine32, "B", ("DisplayName", "B"), ("UninstallString", $"MsiExec.exe /I{ProductKey}"));

        Read();

        Assert.Equal(1, _installer.Queries[Guid.Parse(ProductKey)]);
    }

    [Fact]
    public void ReadingAgainAnEntryThatWentAnswersNull()
    {
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));
        _registry.Remove(key);

        Assert.Null(new InstalledAppsReader(_registry, _installer).ReadAgain(key));
    }

    [Fact]
    public void ReadingAgainAnEntryThatChangedDecidesItAfresh()
    {
        var uninstaller = _temp.CreateFile(1, "Tool", "unins000.exe");
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("UninstallString", $"\"{uninstaller}\""));
        var reader = new InstalledAppsReader(_registry, _installer);
        Assert.False(Assert.Single(reader.Read(CancellationToken.None).Entries).IsStale);

        File.Delete(uninstaller);

        Assert.True(reader.ReadAgain(key)!.IsStale);
    }
}
