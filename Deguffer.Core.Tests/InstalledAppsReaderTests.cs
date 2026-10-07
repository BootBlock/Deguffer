using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading the three <c>Uninstall</c> keys into entries (§7.3): what Windows lists, which list each
/// entry is in, and that a value of an unexpected type never drops an entry.
/// </summary>
public sealed class InstalledAppsReaderTests
{
    private const string ProductKey = "{12345678-9ABC-DEF0-1234-56789ABCDEF0}";

    private const string Apps = @"C:\Users\testuser\Apps";

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeWindowsInstaller _installer = new();

    private readonly FakePathProbe _paths = new();

    private readonly FakePackageDependencies _dependencies = new();

    private InstalledAppsReader Reader => new(_registry, _installer, _paths, FixedSystemDirectories.Standard, _dependencies, new FakeVolumeInventory());

    private InstalledAppsReading Read() => Reader.Read(CancellationToken.None);

    private InstalledEntry Only() => Assert.Single(Read().Entries);

    [Fact]
    public void AnEntryWithAPresentUninstallerIsListedAndInstalled()
    {
        var uninstaller = $@"{Apps}\Tool\unins000.exe";
        _paths.File(uninstaller);
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
        var folder = $@"{Apps}\Gone";
        _registry.With(UninstallScope.CurrentUser, "Gone",
            ("DisplayName", "Gone"), ("UninstallString", $"\"{folder}\\unins000.exe\""), ("InstallLocation", folder));

        Assert.True(Only().IsStale);
    }

    [Fact]
    public void AnEntryWhoseFolderStandsIsNotStale()
    {
        var folder = $@"{Apps}\Broken";
        _paths.Directory(folder);
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

        Assert.Null(Reader.ReadAgain(key));
    }

    [Fact]
    public void ReadingAgainAnEntryThatChangedDecidesItAfresh()
    {
        var uninstaller = $@"{Apps}\Tool\unins000.exe";
        _paths.File(uninstaller);
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("UninstallString", $"\"{uninstaller}\""));
        var reader = Reader;
        Assert.False(Assert.Single(reader.Read(CancellationToken.None).Entries).IsStale);

        _paths.Remove(uninstaller);

        Assert.True(reader.ReadAgain(key)!.IsStale);
    }

    /// <summary>Hundreds of entries share a few executables, folders and package registrations (G4).</summary>
    [Fact]
    public void EachPathAndThePackageRegistrationsAreAskedAboutOnceForOneReading()
    {
        var uninstaller = $@"{Apps}\Shared\unins000.exe";
        _registry.With(UninstallScope.Machine64, "A", ("DisplayName", "A"), ("UninstallString", $"\"{uninstaller}\" /a"), ("InstallLocation", $@"{Apps}\Shared"));
        _registry.With(UninstallScope.Machine32, "B", ("DisplayName", "B"), ("UninstallString", $"\"{uninstaller}\" /b"), ("InstallLocation", $@"{Apps}\Shared"));

        Read();

        Assert.Equal(1, _paths.FileQueries[uninstaller]);
        Assert.Equal(1, _paths.EntryQueries[$@"{Apps}\Shared"]);
        Assert.Equal(1, _paths.DirectoryQueries[@"C:\Users"]);
        Assert.Equal(1, _dependencies.Reads);
    }

    /// <summary>
    /// A 32-bit entry's <c>%ProgramFiles%</c> expands to the 64-bit folder in a 64-bit Deguffer, so
    /// the uninstaller the entry means is found under the 32-bit one.
    /// </summary>
    [Fact]
    public void AnUninstallerStandingInTheOtherProgramFolderIsFound()
    {
        _paths.File(@"C:\Program Files (x86)\Tool\unins000.exe");
        _registry.With(UninstallScope.Machine32, "Tool",
            ("DisplayName", "Tool"), ("UninstallString", @"""C:\Program Files\Tool\unins000.exe"""));

        Assert.Equal(EntryStanding.Installed, Only().Standing.Standing);
    }

    /// <summary>A Burn bundle whose cached setup is gone is judged by the products it installed.</summary>
    [Fact]
    public void ABundleWhoseProductIsStillInstalledIsNotStale()
    {
        const string Bundle = "{0A1B2C3D-0000-0000-0000-000000000001}";
        _installer.With(Guid.Parse(ProductKey), InstallerProductState.Installed);
        _dependencies.Provider("Tool.Core", ProductKey, Bundle);
        _registry.With(UninstallScope.Machine32, Bundle,
            ("DisplayName", "Tool"),
            ("BundleProviderKey", Bundle),
            ("UninstallString", @"""C:\ProgramData\Package Cache\" + Bundle + @"\ToolSetup.exe"" /uninstall"));

        Assert.Equal(EntryStanding.Installed, Only().Standing.Standing);
    }
}
