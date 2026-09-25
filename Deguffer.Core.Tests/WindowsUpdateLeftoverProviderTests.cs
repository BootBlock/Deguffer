using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>$WinREAgent</c> and <c>$GetCurrent</c>, which no Windows cleanup names and Microsoft does not
/// document, offered on Deguffer's own judgement and only on its narrow terms: nothing inside changed
/// for thirty days, and no update left unfinished.
/// </summary>
public sealed class WindowsUpdateLeftoverProviderTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromDays(WindowsUpdateLeftoverProvider.QuietDays + 10);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public WindowsUpdateLeftoverProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Volume => _system.SystemDrive;

    private WindowsUpdateLeftoverProvider CreateProvider(
        FakeWindowsServicing? servicing = null,
        FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            servicing: servicing ?? FakeWindowsServicing.Settled);

    /// <summary>
    /// The shape the audited machine held: a working image, a backup of the old recovery image, and
    /// the rollback state beside them, each dated <paramref name="age"/> ago.
    /// </summary>
    private string Agent(TimeSpan age)
    {
        var agent = Path.Combine(Volume, "$WinREAgent");
        Write(Path.Combine(agent, "Scratch", "update.wim"), 8192, age);
        Write(Path.Combine(agent, "Backup", "winre.wim"), 8192, age);
        Write(Path.Combine(agent, "Rollback.xml"), 512, age);
        Dated(Path.Combine(agent, "Scratch"), age);
        Dated(Path.Combine(agent, "Backup"), age);
        Dated(agent, age);
        return agent;
    }

    private string Assistant(TimeSpan age)
    {
        var assistant = Path.Combine(Volume, "$GetCurrent");
        Write(Path.Combine(assistant, "Logs", "setup.log"), 2048, age);
        Dated(Path.Combine(assistant, "Logs"), age);
        Dated(assistant, age);
        return assistant;
    }

    private static void Write(string file, int bytes, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[bytes]);
        File.SetCreationTimeUtc(file, DateTime.UtcNow - age);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
    }

    private static void Dated(string directory, TimeSpan age)
    {
        Directory.SetCreationTimeUtc(directory, DateTime.UtcNow - age);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - age);
    }

    [Fact]
    public async Task ReportsNotPresentOnADriveHoldingNeither()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The declaration, pinned by name. <c>$SysReset</c> is not here: Windows' own cleanup names its
    /// logs, so §5.1 sends them through that (see <see cref="WindowsServicingLogProviderTests"/>).
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheTwoFoldersNoWindowsCleanupNamesAndNothingElse()
    {
        var root = Assert.Single(CreateProvider().Roots);

        Assert.Equal(Volume, root.Path);
        Assert.Equal(["$WinREAgent", "$GetCurrent"], root.Locations.Select(l => l.RelativePath));
        Assert.All(root.Locations, l => Assert.Equal(DeclaredLocationKind.Directory, l.Kind));
        Assert.Equal(SystemDriveRoot.Survivors, root.ProtectedNames);
        Assert.True(root.RequiresElevation);
    }

    /// <summary>
    /// Quiet for longer than the floor and nothing unfinished: each folder is offered as a removal that
    /// goes whole or not at all, and after the run it is gone and everything beside it is standing.
    /// </summary>
    [Fact]
    public async Task OffersAQuietFolderWholeAndEverythingBesideItSurvives()
    {
        var agent = Agent(Quiet);
        var assistant = Assistant(Quiet);
        var users = Path.Combine(Volume, "Users");
        Write(Path.Combine(users, "testuser", "notes.txt"), 16, TimeSpan.Zero);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.Equal([agent, assistant], plan.TargetedPaths);
        Assert.All(plan.Steps, step => Assert.True(Assert.IsType<DeleteDirectoryStep>(step).IsIndivisible));
        Assert.True(plan.RequiresElevation);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(agent));
        Assert.False(Directory.Exists(assistant));
        Assert.True(Directory.Exists(users));
        Assert.True(Directory.Exists(_system.WindowsDirectory));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The judgement is Deguffer's, and the plan says so in so many words: a reader must never take it
    /// for Microsoft's.
    /// </summary>
    [Fact]
    public async Task ThePlanSaysTheJudgementIsDegufferRatherThanMicrosoft()
    {
        Agent(Quiet);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("Deguffer's judgement", StringComparison.Ordinal));
        Assert.Contains("Deguffer's judgement rather than Microsoft's", plan.WhatHappensOnNextUse, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollback manifest is meaningless without the image it restores. Something recent inside means
    /// the servicing that wrote it may still need the rest, so the whole folder is held back — the old
    /// backup image as well as the new working one — and nothing in it is removed.
    /// </summary>
    [Fact]
    public async Task SomethingRecentInsideHoldsTheWholeFolderBack()
    {
        var agent = Agent(Quiet);
        var backup = Path.Combine(agent, "Backup", "winre.wim");
        var working = Path.Combine(agent, "Scratch", "update.wim");
        Write(working, 8192, TimeSpan.FromDays(9));
        Dated(Path.Combine(agent, "Scratch"), Quiet);
        Dated(agent, Quiet);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(agent, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(working));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task HoldsEverythingBackWhileARestartIsOwed()
    {
        var agent = Agent(Quiet);

        var plan = await CreateProvider(new FakeWindowsServicing { IsRestartPending = true }).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WaitsForAnUpdate);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(agent, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
    }

    [Fact]
    public async Task HoldsEverythingBackWhileTheServicingStackIsRunning()
    {
        Agent(Quiet);

        var plan = await CreateProvider(inspector: new FakeProcessInspector("TiWorker")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WaitsForAnUpdate);
        Assert.Contains(plan.Notes, n => n.Message.Contains("TiWorker", StringComparison.Ordinal));
    }

    /// <summary>A restart that will change something inside one folder holds that folder alone back.</summary>
    [Fact]
    public async Task HoldsBackOnlyTheFolderARestartWillChange()
    {
        var agent = Agent(Quiet);
        var assistant = Assistant(Quiet);
        var servicing = new FakeWindowsServicing { PendingFileOperations = [Path.Combine(agent, "Rollback.xml")] };

        var plan = await CreateProvider(servicing).PlanAsync();

        Assert.Equal([assistant], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(agent, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
    }

    /// <summary>
    /// The floor travels on the plan, so a file written between the preview and the clean is left
    /// where it is rather than taken with the rest.
    /// </summary>
    [Fact]
    public async Task AFileWrittenAfterThePreviewIsLeftStanding()
    {
        var agent = Agent(Quiet);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        Assert.True(plan.Keep.IsOn);

        var arrived = Path.Combine(agent, "Scratch", "arrived.wim");
        File.WriteAllBytes(arrived, new byte[128]);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(arrived));
        Assert.False(File.Exists(Path.Combine(agent, "Backup", "winre.wim")));
    }

    /// <summary>
    /// §5.2 at a volume root: the provider's names are the whole of its reach, so the siblings an
    /// upgrade leaves — which are another provider's to clear through Windows' own cleanup — and a
    /// folder somebody keeps there are never targets.
    /// </summary>
    [Fact]
    public async Task NeverReachesAnythingElseAtTheTopOfTheDrive()
    {
        Agent(Quiet);
        var setup = Path.Combine(Volume, "$Windows.~BT");
        var theirs = Path.Combine(Volume, "$WinREAgent.keep");
        Write(Path.Combine(setup, "setup.log"), 64, Quiet);
        Write(Path.Combine(theirs, "notes.txt"), 64, Quiet);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(setup, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(setup, "setup.log")));
        Assert.True(File.Exists(Path.Combine(theirs, "notes.txt")));
    }
}
