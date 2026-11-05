using System.Runtime.InteropServices;
using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// A package's manifest is read once per full name for the life of the process, except in
/// development mode, while the state is read at every reading (§7.5). The packages here are invented.
/// </summary>
public sealed class PackagedStartupTasksTests
{
    private const string Family = "Contoso.Tool_8wekyb3d8bbwe";

    private const string ToolV1 = "Contoso.Tool_1.0.0.0_x64__8wekyb3d8bbwe";

    private const string ToolV2 = "Contoso.Tool_2.0.0.0_x64__8wekyb3d8bbwe";

    private readonly FakeInstalledPackages packages = new();

    [Fact]
    public void ASecondReadingReadsNoManifestForAnUnchangedFullName()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        packages.Add("Contoso.Quiet_1.0.0.0_x64__8wekyb3d8bbwe", ManifestWithoutTask);
        var reader = new PackagedStartupTasks(packages);

        var first = reader.Read(CancellationToken.None);
        var second = reader.Read(CancellationToken.None);

        Assert.Equal(2, packages.ManifestsRead.Count);
        Assert.Equal(2, packages.FolderLookups);
        Assert.Equal(first.Tasks, second.Tasks);
        Assert.True(second.Complete);
        Assert.Equal(Path.Combine(Folder(ToolV1), @"Tool\Tool.exe"), Assert.Single(second.Tasks).Program);
    }

    [Fact]
    public void AnUpdatedPackageIsReadAgainUnderItsNewFullName()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        var reader = new PackagedStartupTasks(packages);
        reader.Read(CancellationToken.None);

        packages.Remove(ToolV1);
        packages.Add(ToolV2, Manifest("ToolStartupV2", @"Tool\Tool2.exe"));
        var task = Assert.Single(reader.Read(CancellationToken.None).Tasks);

        Assert.Equal("ToolStartupV2", task.TaskId);
        Assert.Equal(Path.Combine(Folder(ToolV2), @"Tool\Tool2.exe"), task.Program);
        Assert.Equal(2, packages.ManifestsRead.Count);
    }

    [Fact]
    public void ADevelopmentModePackageIsReadAtEveryReading()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"), developmentMode: true);
        var reader = new PackagedStartupTasks(packages);
        reader.Read(CancellationToken.None);

        packages.Rewrite(ToolV1, Manifest("RenamedStartup", @"Tool\Tool.exe"));
        var task = Assert.Single(reader.Read(CancellationToken.None).Tasks);

        Assert.Equal("RenamedStartup", task.TaskId);
        Assert.Equal(2, packages.ManifestsRead.Count);
    }

    [Fact]
    public void AChangedStateShowsOnTheSecondReading()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        packages.States[(Family, "ToolStartup")] = 1;
        var reader = new PackagedStartupTasks(packages);
        reader.Read(CancellationToken.None);

        packages.States[(Family, "ToolStartup")] = 2;

        Assert.Equal(2, Assert.Single(reader.Read(CancellationToken.None).Tasks).State);
        Assert.Single(packages.ManifestsRead);
    }

    /// <summary>A path that reaches past <c>MAX_PATH</c> would otherwise be truncated (§6.3).</summary>
    [Fact]
    public void TheManifestIsReadThroughItsExtendedPath()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));

        new PackagedStartupTasks(packages).Read(CancellationToken.None);

        Assert.Equal(@"\\?\" + Path.Combine(Folder(ToolV1), "AppxManifest.xml"), Assert.Single(packages.ManifestsRead));
    }

    public static TheoryData<Exception> Absences => new()
    {
        new FileNotFoundException(),
        new DirectoryNotFoundException(),
    };

    /// <summary>A package with no manifest starts nothing, so it is not a gap in the list.</summary>
    [Theory]
    [MemberData(nameof(Absences))]
    public void AMissingManifestLeavesTheListComplete(Exception absence)
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        packages.Refuse(ToolV1, absence);

        var read = new PackagedStartupTasks(packages).Read(CancellationToken.None);

        Assert.True(read.Complete);
        Assert.Empty(read.Tasks);
    }

    /// <summary>A package caught before its manifest is written must not be remembered as declaring nothing.</summary>
    [Fact]
    public void AMissingManifestIsLookedForAgainAtTheNextReading()
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        packages.Refuse(ToolV1, new FileNotFoundException());
        var reader = new PackagedStartupTasks(packages);
        reader.Read(CancellationToken.None);

        packages.Allow(ToolV1);

        Assert.Equal("ToolStartup", Assert.Single(reader.Read(CancellationToken.None).Tasks).TaskId);
    }

    public static TheoryData<Exception> Refusals => new()
    {
        new UnauthorizedAccessException(),
        new IOException(),
    };

    /// <summary>A manifest that could not be read may declare a task the list lacks, at every reading.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusedManifestLeavesTheListIncompleteAtEveryReading(Exception refusal)
    {
        packages.Add(ToolV1, Manifest("ToolStartup", @"Tool\Tool.exe"));
        packages.Add(ToolV2, Manifest("OtherStartup", @"Tool\Other.exe"));
        packages.Refuse(ToolV1, refusal);
        var reader = new PackagedStartupTasks(packages);

        var first = reader.Read(CancellationToken.None);
        var second = reader.Read(CancellationToken.None);

        Assert.False(first.Complete);
        Assert.False(second.Complete);
        Assert.Equal("OtherStartup", Assert.Single(second.Tasks).TaskId);
    }

    [Fact]
    public void AListWindowsWillNotGiveIsUnreadable()
    {
        packages.ListRefusal = new COMException();

        Assert.Same(PackagedTasksRead.Unreadable, new PackagedStartupTasks(packages).Read(CancellationToken.None));
    }

    private const string ManifestWithoutTask = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications>
            <Application Id="App" Executable="Quiet.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    private static string Manifest(string taskId, string executable) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
          <Applications>
            <Application Id="App" Executable="{executable}" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="{taskId}" Enabled="true" DisplayName="Tool" />
                </uap5:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    private static string Folder(string fullName) => Path.Combine(@"C:\Program Files\WindowsApps", fullName);

    private sealed class FakeInstalledPackages : IInstalledPackages
    {
        private readonly List<FakePackage> installed = [];

        private readonly Dictionary<string, Exception> refusals = new(StringComparer.Ordinal);

        public Dictionary<(string Family, string TaskId), int> States { get; } = [];

        public List<string> ManifestsRead { get; } = [];

        public int FolderLookups { get; private set; }

        public Exception? ListRefusal { get; set; }

        public void Add(string fullName, string manifest, bool developmentMode = false) =>
            installed.Add(new FakePackage(this, fullName, developmentMode) { Manifest = manifest });

        public void Remove(string fullName) => installed.RemoveAll(p => p.FullName == fullName);

        public void Rewrite(string fullName, string manifest) => installed.Single(p => p.FullName == fullName).Manifest = manifest;

        public void Refuse(string fullName, Exception refusal) => refusals[ManifestPath(fullName)] = refusal;

        public void Allow(string fullName) => refusals.Remove(ManifestPath(fullName));

        public IReadOnlyList<IInstalledPackage> List() => ListRefusal is { } refusal ? throw refusal : [.. installed];

        public string ReadManifest(string path)
        {
            ManifestsRead.Add(path);

            if (refusals.TryGetValue(path, out var refusal))
            {
                throw refusal;
            }

            return installed.SingleOrDefault(p => ManifestPath(p.FullName) == path)?.Manifest
                ?? throw new FileNotFoundException(null, path);
        }

        public IStartupTaskStates OpenStates() => new FakeStates(States);

        private static string ManifestPath(string fullName) => @"\\?\" + Path.Combine(Folder(fullName), "AppxManifest.xml");

        private sealed class FakePackage(FakeInstalledPackages owner, string fullName, bool developmentMode) : IInstalledPackage
        {
            public string Manifest { get; set; } = "";

            public string FullName => fullName;

            public string FamilyName => Family;

            public string Name => "Contoso.Tool";

            public bool IsDevelopmentMode => developmentMode;

            public string? DisplayName => "Tool";

            public string? PublisherDisplayName => "Contoso";

            public string? Folder()
            {
                owner.FolderLookups++;

                return PackagedStartupTasksTests.Folder(fullName);
            }
        }

        private sealed class FakeStates(Dictionary<(string Family, string TaskId), int> states) : IStartupTaskStates
        {
            public int? StateOf(string family, string taskId) => states.TryGetValue((family, taskId), out var state) ? state : null;

            public void Dispose()
            {
            }
        }
    }
}
