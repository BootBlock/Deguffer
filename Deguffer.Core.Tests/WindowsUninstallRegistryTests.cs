using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the real reader makes of values the registry itself stores, against a scratch key. A fake
/// would be asserting its own reply: these are the types the platform hands back.
/// </summary>
public sealed class WindowsUninstallRegistryTests : IDisposable
{
    private readonly ScratchKey _scratch = new();

    private readonly RegistryKey _uninstall;

    private readonly WindowsUninstallRegistry _registry;

    public WindowsUninstallRegistryTests()
    {
        _uninstall = _scratch.Key.CreateSubKey("Uninstall");
        _registry = new WindowsUninstallRegistry(
            (_, writable) => Registry.CurrentUser.OpenSubKey($@"{_scratch.Path}\Uninstall", writable));
    }

    public void Dispose()
    {
        _uninstall.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public void ValuesArriveAsTheTypesTheyWereWrittenAs()
    {
        using (var entry = _uninstall.CreateSubKey("Tool"))
        {
            entry.SetValue("DisplayName", "Tool");
            entry.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            entry.SetValue("EstimatedSize", 5L, RegistryValueKind.QWord);
            entry.SetValue("NoRemove", "1", RegistryValueKind.String);
        }

        var values = Assert.Single(_registry.Read(UninstallScope.CurrentUser).Records).Values;

        Assert.Equal("Tool", values.Text("DisplayName"));
        Assert.True(values.Flag("SystemComponent"));
        Assert.Equal(5L, values.Number("EstimatedSize"));
        Assert.False(values.Flag("NoRemove"));
    }

    /// <summary>Windows expands a <c>REG_EXPAND_SZ</c> command before it runs it, so the reader does too.</summary>
    [Fact]
    public void AnExpandableStringArrivesExpanded()
    {
        using (var entry = _uninstall.CreateSubKey("Tool"))
        {
            entry.SetValue("UninstallString", @"%SystemRoot%\unins.exe", RegistryValueKind.ExpandString);
        }

        var values = Assert.Single(_registry.Read(UninstallScope.CurrentUser).Records).Values;

        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\unins.exe"), values.Text("UninstallString"));
    }

    [Fact]
    public void AnEntryReadAgainHoldsTheSameValues()
    {
        using (var entry = _uninstall.CreateSubKey("Tool"))
        {
            entry.SetValue("DisplayName", "Tool");
            entry.SetValue("Blob", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        }

        var first = Assert.Single(_registry.Read(UninstallScope.CurrentUser).Records);
        var (presence, again) = _registry.ReadOne(first.Key);

        Assert.Equal(PathPresence.Present, presence);
        Assert.True(first.Values.SameAs(again));
    }

    [Fact]
    public void AChangedValueIsNotTheSame()
    {
        using var entry = _uninstall.CreateSubKey("Tool");
        entry.SetValue("DisplayName", "Tool");
        var first = Assert.Single(_registry.Read(UninstallScope.CurrentUser).Records);

        entry.SetValue("DisplayName", "Other");

        Assert.False(first.Values.SameAs(_registry.ReadOne(first.Key).Values));
    }

    [Fact]
    public void AnEntryThatIsNotThereIsAbsent()
    {
        Assert.Equal(PathPresence.Absent, _registry.ReadOne(new UninstallKey(UninstallScope.CurrentUser, "Nothing")).Presence);
    }

    [Fact]
    public void AnUninstallKeyThatIsNotThereIsAbsent()
    {
        var missing = new WindowsUninstallRegistry((_, _) => Registry.CurrentUser.OpenSubKey($@"{_scratch.Path}\Missing"));

        Assert.Equal(PathPresence.Absent, missing.Read(UninstallScope.CurrentUser).Presence);
    }

    /// <summary>§7.3: only the entry's own key goes, with the keys below it.</summary>
    [Fact]
    public void DeletingAnEntryTakesItsSubtreeAndNothingBeside()
    {
        using (var entry = _uninstall.CreateSubKey("Tool"))
        using (entry.CreateSubKey("Child"))
        {
        }

        _uninstall.CreateSubKey("Neighbour").Dispose();

        _registry.Delete(new UninstallKey(UninstallScope.CurrentUser, "Tool"));

        Assert.Equal(["Neighbour"], _uninstall.GetSubKeyNames());
    }
}
