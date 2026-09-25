using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Updater downloads go only while their application is not running, the Visual Studio Installer's
/// staging folder is found through its own state file and nowhere else, and Blender's unsaved work
/// is recognised so that it is kept.
/// </summary>
public sealed class TempInstallerDownloadProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TempInstallerDownloadProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempInstallerDownloadProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive);

    private string Entry(int bytes, params string[] segments) => _temp.CreateFile(bytes, ["temp", .. segments]);

    /// <summary>Writes the state file an installed Visual Studio instance keeps for this account.</summary>
    private void VisualStudioStagesIn(string staging)
    {
        var instance = Path.Combine(
            _environment.LocalAppData, "Microsoft", "VisualStudio", "Packages", "_Instances", "0c9e2d71");
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(instance, "state.json"),
            JsonSerializer.Serialize(new { temporaryCache = staging, userProperties = new { } }));
    }

    [Fact]
    public async Task TakesAnUpdaterDownloadAndLeavesVsCodesLiveFoldersAlone()
    {
        Entry(4096, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe");
        Entry(2048, "DockerDesktopUpdates", "Docker Desktop Installer (223695).exe");
        var typescript = Entry(1024, "vscode-typescript", "tscancellation-1.tmp");
        var zip = Entry(512, "vscode-zip-merge-1", "x");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(4096 + 2048, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(Path.Combine(UserTemp, "vscode-stable-user-x64")));
        Assert.True(File.Exists(typescript), "the TypeScript server's working folder was removed");
        Assert.True(File.Exists(zip), "a VS Code folder that is not the updater's was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// VS Code applies a downloaded update from its folder when it restarts, so the folder waits for
    /// VS Code to close.
    /// </summary>
    [Fact]
    public async Task LeavesTheVsCodeDownloadAloneWhileVsCodeRuns()
    {
        var update = Path.GetDirectoryName(Entry(4096, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe"))!;

        var plan = await CreateProvider(new FakeProcessInspector("Code")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(update, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The staging folder's name is random, so only the installer's own state file identifies it —
    /// and inside it only the packages it staged go. The folder and anything else in it stay (§5.2).
    /// </summary>
    [Fact]
    public async Task TakesTheVisualStudioInstallersStagedPackagesFoundThroughItsStateFile()
    {
        var staging = Path.Combine(UserTemp, "q4mzt0xk");
        Entry(4096, "q4mzt0xk", "Win11SDK_10.0.26100.E9BB0EB40C39C3B4B64C", "Installers", "a.msi");
        var other = Entry(1024, "q4mzt0xk", "notes", "x.txt");
        var lookalike = Entry(2048, "k2mzqv0p", "Win11SDK_10.0.26100.E9BB0EB40C39C3B4B64C", "Installers", "a.msi");
        VisualStudioStagesIn(staging);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Equal(Path.Combine(staging, "Win11SDK_10.0.26100.E9BB0EB40C39C3B4B64C"), step.Path);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(staging, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(other), "something in the staging folder the installer did not stage was removed");
        Assert.True(File.Exists(lookalike), "a random folder that only looks like staging was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Equal([staging], await provider.ClaimedEntriesAsync([UserTemp]));
    }

    /// <summary>A state file naming a folder outside the temporary folders is not followed.</summary>
    [Fact]
    public async Task IgnoresAStagingFolderOutsideTheTemporaryFolders()
    {
        var elsewhere = _temp.CreateDirectory("profile", "Documents");
        _temp.CreateFile(4096, "profile", "Documents", "Win11SDK_10.0.26100.E9BB0EB40C39C3B4B64C", "a.msi");
        VisualStudioStagesIn(elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>
    /// A crashed session's folder goes once Blender is closed, and the recovery files beside it are
    /// recognised so they are kept and claimed: the temporary-folder row would otherwise take them on
    /// their age.
    /// </summary>
    [Fact]
    public async Task TakesACrashedBlenderSessionAndKeepsItsRecoveryFiles()
    {
        Entry(4096, "blender_a12345", "cache", "bake.bphys");
        var quit = Entry(2048, "quit.blend");
        var autosave = Entry(1024, "scene_4242_autosave.blend");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(UserTemp, "blender_a12345"), Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>()).Path);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(quit, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(autosave, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(quit));
        Assert.True(File.Exists(autosave));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        var claimed = await provider.ClaimedEntriesAsync([UserTemp]);
        Assert.Contains(quit, claimed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(autosave, claimed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeavesEveryBlenderSessionAloneWhileBlenderRuns()
    {
        Entry(4096, "blender_a12345", "cache", "bake.bphys");

        var plan = await CreateProvider(new FakeProcessInspector("blender")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }
}
