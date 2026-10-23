using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// The crash dump row against a machine whose settings send dumps somewhere other than the defaults.
///
/// <para>A moved folder can be any folder on the machine, so the negative is the assertion that
/// matters in every test here: the folder, its other files, a file named like a dump that is not one,
/// and anything below it are all still there after a run. Each setting is moved through the registry
/// seam, one test each, because each is read from its own key.</para>
/// </summary>
public sealed class RelocatedCrashDumpTests : IDisposable
{
    private const string CrashControl = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string LocalDumps = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public RelocatedCrashDumpTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A second drive, in the only form a test may have one: a folder no default names.</summary>
    private string Elsewhere => Path.Combine(_temp.Path, "elsewhere");

    private CrashDumpProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system, volumes: new FakeVolumeInventory());

    private void Set(string key, string value, string data) =>
        _environment.WithMachineRegistryValue(key, value, data, RegistryView.Registry64);

    /// <summary>A file that starts as a dump of <paramref name="signature"/>'s kind does.</summary>
    private static string Dump(string path, string signature = "PAGEDU64", int bytes = 65536)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var content = new byte[bytes];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(content, 0);
        File.WriteAllBytes(path, content);

        return path;
    }

    private static string Text(string path, string content = "keep me")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>
    /// The case the report named: <c>DumpFile</c> on another drive, holding a dump of many gigabytes,
    /// while the row read "Already clear". The one file is offered, and the folder it sits in, which
    /// on a real machine is a drive root, survives with everything else in it.
    /// </summary>
    [Fact]
    public async Task AMovedFullDumpIsOfferedByNameAndEverythingBesideItSurvives()
    {
        var memoryDump = Dump(Path.Combine(Elsewhere, "MEMORY.DMP"), bytes: 131072);
        var neighbour = Text(Path.Combine(Elsewhere, "notes.txt"));
        Set(CrashControl, "DumpFile", memoryDump);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        var step = Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps));
        Assert.Equal(memoryDump, step.Path);
        Assert.Equal(131072, step.EstimatedBytes);
        Assert.True(step.RequiresElevation);
        Assert.Contains(plan.Notes, n => n.Message.Contains(memoryDump, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n == DeclaredLocations.ElevationNote);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Elsewhere, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(memoryDump));
        Assert.True(File.Exists(neighbour), "a file beside the moved dump was removed");
        Assert.True(Directory.Exists(Elsewhere), "the folder holding the moved dump was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>MinidumpDir</c> names a folder, and only the dumps directly inside it are offered: each by
    /// name, never the folder. A file named like a dump without a dump's header, a file of another
    /// kind, and a dump one folder down are all somebody else's.
    /// </summary>
    [Fact]
    public async Task AMovedMinidumpFolderOffersOnlyTheDumpsDirectlyInsideIt()
    {
        var folder = Path.Combine(Elsewhere, "Minidumps");
        var kernel = Dump(Path.Combine(folder, "102226-01.dmp"));
        var application = Dump(Path.Combine(folder, "app.dmp"), signature: "MDMP");
        var impostor = Text(Path.Combine(folder, "readme.dmp"));
        var other = Text(Path.Combine(folder, "report.txt"));
        var nested = Dump(Path.Combine(folder, "kept", "older.dmp"));
        Set(CrashControl, "MinidumpDir", folder);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { application, kernel }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps, s => Assert.IsType<DeleteFileStep>(s));
        Assert.All(plan.Steps.OfType<DeleteStep>(), s => Assert.True(s.RequiresElevation));
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(provider));

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(kernel));
        Assert.False(File.Exists(application));

        foreach (var spared in new[] { impostor, other, nested })
        {
            Assert.True(File.Exists(spared), $"{Path.GetFileName(spared)} was removed");
        }

        Assert.True(Directory.Exists(folder), "the moved folder itself was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// <c>LocalDumps</c> has a folder for every application and one for each application that names
    /// its own. Both are read, and both hold dumps the signed-in account may remove.
    /// </summary>
    [Fact]
    public async Task MovedApplicationDumpFoldersAreReadForEveryApplicationAndNeedNoElevation()
    {
        var shared = Dump(Path.Combine(Elsewhere, "AppDumps", "first.exe.1234.dmp"), signature: "MDMP");
        var own = Dump(Path.Combine(Elsewhere, "EditorDumps", "editor.exe.5678.dmp"), signature: "MDMP");
        Set(LocalDumps, "DumpFolder", Path.GetDirectoryName(shared)!);
        Set($@"{LocalDumps}\editor.exe", "DumpFolder", Path.GetDirectoryName(own)!);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { shared, own }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps.OfType<DeleteStep>(), s => Assert.False(s.RequiresElevation));
        Assert.Contains(plan.Notes, n => n.Message.Contains("editor.exe", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder the user keeps their own files in is not looked into, even where a setting sends
    /// dumps there and a real dump is in it: a dump somebody saved beside their documents is theirs.
    /// The row says where the dumps go and why it stopped, and does not read as clear.
    /// </summary>
    [Fact]
    public async Task AMovedFolderThatIsTheUsersOwnIsNamedAndNeverLookedInto()
    {
        var documents = _environment.Documents!;
        var saved = Dump(Path.Combine(documents, "for-the-bug-report.dmp"), signature: "MDMP");
        Set(LocalDumps, "DumpFolder", documents);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(documents, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("does not look there", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(saved));
    }

    /// <summary>
    /// Windows writes the kernel's two values on every machine, set to the defaults. A setting that
    /// names a declared location is that location, so the dump is planned once and nothing is said
    /// about a move that did not happen.
    /// </summary>
    [Fact]
    public async Task ASettingThatNamesItsDefaultIsNotAMove()
    {
        var windows = _system.WindowsDirectory;
        var memoryDump = Dump(Path.Combine(windows, "MEMORY.DMP"));
        var minidumps = Path.Combine(windows, "Minidump");
        Dump(Path.Combine(minidumps, "1.dmp"));
        Set(CrashControl, "DumpFile", memoryDump);
        Set(CrashControl, "MinidumpDir", minidumps + Path.DirectorySeparatorChar);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { memoryDump, minidumps }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("crash settings", StringComparison.Ordinal));
    }

    /// <summary>
    /// The full kernel dump is readable only by an administrator, so an unelevated run cannot read its
    /// header. A refusal is not evidence of what a file is, so the file is named and left alone, and
    /// the row does not read as clear over it.
    /// </summary>
    [Fact]
    public async Task AMovedDumpThatCannotBeReadIsNamedAndNotOffered()
    {
        var memoryDump = Dump(Path.Combine(Elsewhere, "MEMORY.DMP"));
        Set(CrashControl, "DumpFile", memoryDump);

        CleanupPlan plan;

        using (DeniedDirectory.WithUnreadableContent(memoryDump))
        {
            plan = await CreateProvider().PlanAsync();
        }

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file a setting names that is not a dump is somebody else's, whatever the setting says.
    /// Nothing is offered, and the row says what it found there.
    /// </summary>
    [Fact]
    public async Task AMovedDumpFileThatIsNotADumpIsLeftAlone()
    {
        var notADump = Text(Path.Combine(Elsewhere, "MEMORY.DMP"));
        Set(CrashControl, "DumpFile", notADump);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a crash dump", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(notADump));
    }

    /// <summary>
    /// A moved folder reached through a link is not looked through, at any level above it, for the
    /// reason no declared location is: the dumps on the far side would be removed while every
    /// survivor named for them resolved through the link and passed.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task AMovedFolderBehindALinkIsNeverLookedThrough(DirectoryLinkKind kind)
    {
        var farSide = Path.Combine(_temp.Path, "far");
        var bystander = Dump(Path.Combine(farSide, "Minidumps", "1.dmp"));
        var link = Path.Combine(_temp.Path, "linked");
        DirectoryLink.Create(kind, link, farSide);
        Set(CrashControl, "MinidumpDir", Path.Combine(link, "Minidumps"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "a dump was removed through a link");
    }

    /// <summary>
    /// The empty row used to say that Windows had written no dumps "on this machine" after looking
    /// only in the defaults. It now names each moved place it looked in, so its claim covers them.
    /// </summary>
    [Fact]
    public async Task AnEmptyRowNamesEveryMovedPlaceItLookedIn()
    {
        var folder = Path.Combine(Elsewhere, "Minidumps");
        Directory.CreateDirectory(folder);
        var absent = Path.Combine(Elsewhere, "MEMORY.DMP");
        Set(CrashControl, "MinidumpDir", folder);
        Set(CrashControl, "DumpFile", absent);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains(absent, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("on this machine", StringComparison.Ordinal));
    }

    /// <summary>
    /// A relative value is resolved by whichever process reads it, which is not Deguffer, so it names
    /// no place Deguffer can look in. It is reported rather than guessed at.
    /// </summary>
    [Fact]
    public async Task ASettingThatIsNotAFullPathIsReportedAndNotGuessedAt()
    {
        Set(LocalDumps, "DumpFolder", "dumps");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }
}
