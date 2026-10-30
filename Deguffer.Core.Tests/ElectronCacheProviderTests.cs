using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Electron's downloader keeps one folder per cached download, named by the SHA-256 of the address it
/// came from, and older downloaders left other layouts in the same folder. So the rule is the hash and
/// nothing else, and most of these are about what is left alone.
/// </summary>
public sealed class ElectronCacheProviderTests : IDisposable
{
    /// <summary>
    /// The folder <c>installation.md</c> shows for Electron 15.3.1: the SHA-256 of
    /// <c>https://github.com/electron/electron/releases/download/v15.3.1</c>.
    /// </summary>
    private const string Download = "523ee27feac8b931299c79e78b7ca4f365aa3f7069fff666cae93c7fb9ff2fee";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ElectronCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private ElectronCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system);

    private string Root => Path.Combine(_environment.LocalAppData, "electron", "Cache");

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        return path;
    }

    private static string CreateDownload(string root, string hash, string zip = "electron-v15.3.1-win32-x64.zip")
    {
        var folder = Path.Combine(root, hash);
        CreateFile(Path.Combine(folder, zip));
        return folder;
    }

    [Fact]
    public async Task ReportsNotPresentWhenElectronHasNeverDownloaded()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// A download is offered whole, under the zip's name, which is the version the user recognises.
    /// Every layout an older downloader left beside it is asserted to survive (§5.6): a zip directly
    /// in the root, a folder named after the whole address, a checksum file, an interrupted download,
    /// and names that are nearly a hash, in capitals or a character short.
    /// </summary>
    [Fact]
    public async Task OffersOnlyFoldersNamedByTheDownloadsHash()
    {
        var download = CreateDownload(Root, Download);
        var legacyZip = CreateFile(Path.Combine(Root, "electron-v6.0.0-win32-x64.zip"));
        var checksums = CreateFile(Path.Combine(Root, "SHASUMS256.txt-6.0.0"));
        var partial = CreateFile(Path.Combine(Root, "tmp-1234-0-electron-v6.0.0-win32-x64.zip"));
        var addressFolder = CreateDownload(Root, "httpsgithub.comelectronelectronreleasesdownloadv6.0.0electron-v6.0.0-win32-x64.zip");
        var capitals = CreateDownload(Root, new string('A', 64));
        var short63 = CreateDownload(Root, Download[..63]);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([download], plan.TargetedPaths);

        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Equal(new ItemIdentity(Download, "electron-v15.3.1-win32-x64.zip"), step.Identity);
        Assert.Contains(step.Facets!, f => f.Value == "electron-v15.3.1-win32-x64.zip");

        foreach (var survivor in new[] { Root, addressFolder, capitals, short63 })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(download));

        foreach (var folder in new[] { addressFolder, capitals, short63 })
        {
            Assert.True(Directory.Exists(folder), $"{folder} was removed.");
        }

        foreach (var file in new[] { legacyZip, checksums, partial })
        {
            Assert.True(File.Exists(file), $"{file} was removed.");
        }
    }

    /// <summary>
    /// The declaration Explore reads says the same: a hash folder, and nothing else, and never a file
    /// carrying a hash's name.
    /// </summary>
    [Fact]
    public void TheToolRootRecognisesOnlyAHashFolder()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Root, root.Path);
        Assert.True(root.Recognises(new ToolRootChild(Download, ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild(Download, ChildKind.File)));
        Assert.False(root.Recognises(new ToolRootChild(Download.ToUpperInvariant(), ChildKind.Folder)));
        Assert.False(root.Recognises(new ToolRootChild("electron-v6.0.0-win32-x64.zip", ChildKind.File)));
    }

    [Fact]
    public async Task HonoursTheCacheVariable()
    {
        var moved = Path.Combine(_temp.Path, "caches", "electron");
        var download = CreateDownload(moved, Download);
        CreateDownload(Root, Download);
        _environment.WithEnvironmentVariable(ElectronCacheProvider.CacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([download], plan.TargetedPaths);
        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// The install script resolves a relative value against its own working directory, which Deguffer is
    /// not, and the default location is not what the user asked for either.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheCacheVariableIsRelative()
    {
        CreateDownload(Root, Download);
        _environment.WithEnvironmentVariable(ElectronCacheProvider.CacheVariable, @"..\electron-cache");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.ToolRoots);

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateDownload(Root, Download);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>A plan holds display paths; the I/O beneath it extends them where it acts (§6.3).</summary>
    [Fact]
    public async Task ThePlanHoldsDisplayPaths()
    {
        CreateDownload(Root, Download);

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);
        Assert.All(plan.TargetedPaths, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));
    }
}
