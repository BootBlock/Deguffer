using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>What a selection of installed programs may uninstall, and what the page says about it (§7.3).</summary>
public sealed class UninstallSelectionTests : IDisposable
{
    private const string Msiexec = @"C:\Windows\System32\msiexec.exe";

    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakePathProbe _paths = new();

    public void Dispose() => _temp.Dispose();

    private InstalledAppsReader Reader => new(_registry, new FakeWindowsInstaller(), _paths, FixedSystemDirectories.Standard, new FakePackageDependencies());

    private ProgramUninstaller Uninstaller => new(Reader, new FakeUninstallLauncher(), Msiexec);

    private InstalledEntry Installed(string name)
    {
        var uninstaller = Path.Combine(_temp.Path, name, "unins000.exe");
        _paths.File(uninstaller);
        return Reader.ReadAgain(_registry.With(UninstallScope.CurrentUser, name, ("DisplayName", name), ("UninstallString", $"\"{uninstaller}\"")))!;
    }

    private InstalledEntry NoRemove(string name) => Installed(name) with { NoRemove = true };

    [Fact]
    public void NothingSelectedRunsNothingAndSaysNothing()
    {
        var selection = UninstallSelection.For([], Uninstaller.Judge);

        Assert.Empty(selection.Runnable);
        Assert.Equal(string.Empty, selection.Note);
    }

    [Fact]
    public void OneProgramSaysTheCommandItRuns()
    {
        var entry = Installed("Tool");

        var selection = UninstallSelection.For([entry], Uninstaller.Judge);

        Assert.Equal($"Runs \"{Path.Combine(_temp.Path, "Tool", "unins000.exe")}\".", selection.Note);
    }

    [Fact]
    public void SeveralProgramsRunInTheOrderSelected()
    {
        var selection = UninstallSelection.For([Installed("B"), Installed("A")], Uninstaller.Judge);

        Assert.Equal(["B", "A"], selection.Runnable.Select(p => p.Entry.Name));
        Assert.StartsWith("Runs 2 uninstallers, one at a time", selection.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedProgramIsLeftAndSaysWhy()
    {
        var selection = UninstallSelection.For([Installed("A"), NoRemove("B")], Uninstaller.Judge);

        Assert.Equal(["A"], selection.Runnable.Select(p => p.Entry.Name));
        Assert.Contains("Runs the uninstaller of 'A'.", selection.Note, StringComparison.Ordinal);
        Assert.Contains("'B' will be left. The entry says it cannot be uninstalled", selection.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyRefusedProgramsSayTheRefusalAlone()
    {
        var selection = UninstallSelection.For([NoRemove("B")], Uninstaller.Judge);

        Assert.Empty(selection.Runnable);
        Assert.StartsWith("The entry says it cannot be uninstalled", selection.Note, StringComparison.Ordinal);
    }

    /// <summary>The second decision reads the entry again, so the command confirmed is the one there now.</summary>
    [Fact]
    public void PreparingReadsTheCommandAgain()
    {
        var entry = Installed("Tool");
        var moved = Path.Combine(_temp.Path, "Moved", "unins000.exe");
        _paths.File(moved);
        _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("UninstallString", $"\"{moved}\""));

        var selection = UninstallSelection.For([entry], Uninstaller.Prepare);

        Assert.Equal(moved, Assert.Single(selection.Runnable).Launch.FileName);
    }

    [Fact]
    public void AProgramWhoseUninstallerVanishedIsLeftWhenPrepared()
    {
        var entry = Installed("Tool");
        _paths.Remove(Path.Combine(_temp.Path, "Tool", "unins000.exe"));

        var selection = UninstallSelection.For([entry], Uninstaller.Prepare);

        Assert.Empty(selection.Runnable);
        Assert.Equal(PathPresence.Absent, ((ProgramCommand)Reader.ReadAgain(entry.Key)!.Command).Presence);
    }
}
