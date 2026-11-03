using System.IO.Compression;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The zips STM32CubeMX and STM32CubeIDE keep beside the firmware packages they unpacked. What has to
/// be shown is that a zip goes only where its name, the package folder beside it and its own contents
/// agree, that every package, <c>Packs</c> and anything unrecognised survive and are asserted, that
/// nothing is offered while ST's tools run, and that the repository STM32CubeMX's settings name is
/// followed only where it may be.
/// </summary>
public sealed class Stm32CubeFirmwareZipProviderTests : IDisposable
{
    private const string F4 = "STM32Cube_FW_F4_V1.27.0";

    private const string F4Zip = "stm32cube_fw_f4_v1270.zip";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public Stm32CubeFirmwareZipProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _volumes.With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Repository => Stm32CubeRepositories.DefaultFolder(_environment);

    private string SettingsFile => Stm32CubeRepositories.SettingsFile(_environment);

    private Stm32CubeFirmwareZipProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, volumes: _volumes);

    private static string Write(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>An unpacked package, as ST's tools leave it: a folder holding its <c>package.xml</c> and its sources.</summary>
    private static string Package(string repository, string name)
    {
        Write(Path.Combine(repository, name, "package.xml"), 512);
        Write(Path.Combine(repository, name, "Drivers", "CMSIS", "core_cm4.h"));
        return Path.Combine(repository, name);
    }

    /// <summary>A zip holding <paramref name="folders"/> as top-level folders, each with a <c>package.xml</c> and a source.</summary>
    private static string Zip(string path, params string[] folders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var folder in folders)
            {
                zip.CreateEntry($"{folder}/");
                Fill(zip.CreateEntry($"{folder}/package.xml"), 512);
                Fill(zip.CreateEntry($"{folder}/Drivers/CMSIS/core_cm4.h"), 4096);
            }
        }

        return path;

        static void Fill(ZipArchiveEntry entry, int bytes)
        {
            using var stream = entry.Open();
            stream.Write(new byte[bytes]);
        }
    }

    /// <summary>
    /// A repository as researched: one package unpacked beside its zip, which goes. Beside them, all of
    /// which stays: a package with no zip, a zip with no package, a zip that holds something other than
    /// the package its name maps to, a zip whose package folder has no <c>package.xml</c>, a download
    /// from ST's website kept under its own name, a zip that is not a zip, <c>Packs</c>, and an image.
    /// </summary>
    /// <returns>The zip that goes, and everything that stays.</returns>
    private (string Offered, string[] Kept) CreateRepository()
    {
        Package(Repository, F4);
        var offered = Zip(Path.Combine(Repository, F4Zip), F4);

        Package(Repository, "STM32Cube_FW_H7_V1.11.2");
        Package(Repository, "STM32Cube_FW_L4_V1.18.0");
        Write(Path.Combine(Repository, "STM32Cube_FW_G0_V1.6.2", "Drivers", "a.c"));

        return (
            offered,
            [
                Path.Combine(Repository, F4, "package.xml"),
                Path.Combine(Repository, "STM32Cube_FW_H7_V1.11.2", "package.xml"),
                Zip(Path.Combine(Repository, "stm32cube_fw_wb_v1210.zip"), "STM32Cube_FW_WB_V1.21.0"),
                Zip(Path.Combine(Repository, "stm32cube_fw_l4_v1180.zip"), "STM32Cube_FW_L4_V1.18.0", "Extra"),
                Zip(Path.Combine(Repository, "stm32cube_fw_g0_v162.zip"), "STM32Cube_FW_G0_V1.6.2"),
                Zip(Path.Combine(Repository, "en.stm32cubef4_v1-27-0.zip"), F4),
                Write(Path.Combine(Repository, "stm32cube_fw_h7_v1112.zip")),
                Write(Path.Combine(Repository, "Packs", "STMicroelectronics", "X-CUBE-AI", "10.2.0", "pack.pdsc")),
                Write(Path.Combine(Repository, "en.prom-stm32h5-mcu-finder.jpg")),
            ]);
    }

    [Fact]
    public async Task ReportsNotPresentWhereNoRepositoryIs()
    {
        Write(Path.Combine(_environment.UserProfile, "STM32Cube", "readme.txt"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>A repository holding packages and no zip of one has nothing this row may take.</summary>
    [Fact]
    public async Task ARepositoryOfPackagesAloneIsNotPresence()
    {
        Package(Repository, F4);
        Write(Path.Combine(Repository, "Packs", "ARM", "mbedTLS", "pack.pdsc"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §3. Patching a package reads its zip again, so the row is Tier 2, and it is researched, so it is
    /// never ticked. Each zip is an item named by its package, kept by its name, under its repository.
    /// </summary>
    [Fact]
    public async Task PlansOnlyTheZipOfAnUnpackedPackageAsATier2Item()
    {
        var (offered, _) = CreateRepository();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([offered], plan.TargetedPaths);
        var step = Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps));
        Assert.Equal(new ItemIdentity(F4Zip, F4), step.Identity);
        Assert.Equal(Repository, step.Group);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.Equal(ProviderBasis.Researched, provider.Basis);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// Each zip that is named as a download and not offered is said, with why: no package beside it,
    /// a zip that holds more than its package, a package folder with no <c>package.xml</c>, and a file
    /// that is not a zip at all.
    /// </summary>
    [Theory]
    [InlineData("stm32cube_fw_wb_v1210.zip", "is not beside it")]
    [InlineData("stm32cube_fw_l4_v1180.zip", "holds something other than STM32Cube_FW_L4_V1.18.0")]
    [InlineData("stm32cube_fw_g0_v162.zip", "is not beside it")]
    [InlineData("stm32cube_fw_h7_v1112.zip", "could not read what it holds")]
    public async Task SaysWhyAZipNamedAsADownloadIsKept(string zip, string why)
    {
        CreateRepository();

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains($"'{zip}'", StringComparison.Ordinal) && n.Message.Contains(why, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Repository, zip), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6, proved by running a plan. The zip goes, and the repository, every package, every zip that
    /// is not offered, <c>Packs</c> and the image all survive, each asserted rather than merely omitted.
    /// </summary>
    [Fact]
    public async Task EverythingButTheOfferedZipSurvivesARunAndIsAsserted()
    {
        var (offered, kept) = CreateRepository();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var name in new[]
        {
            "", F4, "STM32Cube_FW_H7_V1.11.2", "STM32Cube_FW_L4_V1.18.0", "STM32Cube_FW_G0_V1.6.2", "Packs",
            "stm32cube_fw_wb_v1210.zip", "stm32cube_fw_l4_v1180.zip", "stm32cube_fw_g0_v162.zip",
            "en.stm32cubef4_v1-27-0.zip", "stm32cube_fw_h7_v1112.zip", "en.prom-stm32h5-mcu-finder.jpg",
        })
        {
            var path = name.Length == 0 ? Repository : Path.Combine(Repository, name);

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.Contains(
            "Your projects use it",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Repository, F4), StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(offered));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the zip."));
    }

    /// <summary>
    /// <c>V1.1.15</c> and <c>V1.11.5</c> both map to <c>v1115</c>. With both unpacked, the zip's own
    /// contents say which package it is, and it is offered as that one.
    /// </summary>
    [Fact]
    public async Task TwoPackagesSharingADownloadNameAreToldApartByTheZip()
    {
        Package(Repository, "STM32Cube_FW_F0_V1.1.15");
        Package(Repository, "STM32Cube_FW_F0_V1.11.5");
        var zip = Zip(Path.Combine(Repository, "stm32cube_fw_f0_v1115.zip"), "STM32Cube_FW_F0_V1.11.5");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([zip], plan.TargetedPaths);
        Assert.Equal("STM32Cube_FW_F0_V1.11.5", Assert.Single(plan.Steps).Identity!.Name);
    }

    /// <summary>
    /// §5.3. ST's tools download, unpack and patch in the repository, so while either runs nothing is
    /// offered, the repository is named for why, the row does not read as clear, and Explore refuses it.
    /// </summary>
    [Theory]
    [InlineData("STM32CubeMX")]
    [InlineData("stm32cubeide")]
    public async Task NothingIsOfferedWhileSTsToolsRun(string process)
    {
        var (offered, _) = CreateRepository();

        var provider = CreateProvider(new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(
            "is running",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Repository, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);

        var policy = new ExploreActionPolicy(
            [], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(offered).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(offered));
    }

    /// <summary>The clean asks again, so STM32CubeMX opened while the preview was on screen holds the zip back.</summary>
    [Fact]
    public async Task AToolStartedAfterThePreviewHoldsTheZipBack()
    {
        var (offered, _) = CreateRepository();

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        inspector.WithRunning("STM32CubeMX");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(offered), "The zip was removed under an STM32CubeMX started after the preview.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7.1. Explore may take from the repository only the zip the plan offers, as a file, and never a
    /// package, <c>Packs</c> or a zip the plan keeps.
    /// </summary>
    [Fact]
    public async Task ExploreMayTakeOnlyTheOfferedZipFromTheRepository()
    {
        CreateRepository();

        var repository = Assert.Single(await CreateProvider().DiscoverToolRootsAsync());

        Assert.Equal(Repository, repository.Path);
        Assert.True(repository.Recognises(new ToolRootChild(F4Zip, ChildKind.File)));
        Assert.False(repository.Recognises(new ToolRootChild(F4Zip, ChildKind.Folder)));
        Assert.False(repository.Recognises(new ToolRootChild("stm32cube_fw_wb_v1210.zip", ChildKind.File)));
        Assert.False(repository.RecognisesFolder(F4));
        Assert.False(repository.RecognisesFolder("Packs"));
    }

    /// <summary>
    /// STM32CubeMX's Updater Settings move the repository. The folder they name is followed, in both the
    /// plain form and Java's escaped form, and the default is still read beside it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowsTheRepositoryTheUpdaterSettingsName(bool escaped)
    {
        var moved = Path.Combine(_temp.Path, "Tools", "STM32Repo");
        var value = escaped ? moved.Replace(@"\", @"\\", StringComparison.Ordinal).Replace(":", @"\:", StringComparison.Ordinal) : moved;
        Write(SettingsFile, 0);
        File.WriteAllText(SettingsFile, $"LicenseAgreed=true\nRepositoryPath={value}\nCheckUpdate=true\n");
        Package(moved, F4);
        var movedZip = Zip(Path.Combine(moved, F4Zip), F4);
        var (offered, _) = CreateRepository();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { offered, movedZip }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(moved, F4), StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(movedZip));
        Assert.True(File.Exists(Path.Combine(moved, F4, "package.xml")));
    }

    /// <summary>Settings naming the default folder in another spelling examine it once.</summary>
    [Fact]
    public async Task SettingsNamingTheDefaultReadItOnce()
    {
        CreateRepository();
        Write(SettingsFile, 0);
        File.WriteAllText(SettingsFile, $"RepositoryPath={Repository.ToUpperInvariant()}\\\n");

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.Steps);
    }

    /// <summary>
    /// A setting naming the profile, or one that is not a full path, is said out loud and nothing in it
    /// is offered. A zip and package left in the profile itself stay where they are.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaysSoWhenTheSettingsNameAFolderDegufferWillNotTake(bool profile)
    {
        var value = profile ? _environment.UserProfile : @"STM32Cube\Repository";
        Write(SettingsFile, 0);
        File.WriteAllText(SettingsFile, $"RepositoryPath={value}\n");
        Package(_environment.UserProfile, F4);
        var zip = Zip(Path.Combine(_environment.UserProfile, F4Zip), F4);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Updater Settings", StringComparison.Ordinal));
        Assert.Empty(await provider.DiscoverToolRootsAsync());

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(zip));
    }

    /// <summary>
    /// A repository a team shares from a server may be the one another computer is patching from, which
    /// no process table here can show. It is named, not taken, and the row does not read as clear.
    /// </summary>
    [Fact]
    public async Task LeavesARepositoryThatIsNotOnALocalDiskAlone()
    {
        var share = Path.Combine(_temp.Path, "share");
        _volumes.Without(_temp.Path).With(_environment.UserProfile);
        Write(SettingsFile, 0);
        File.WriteAllText(SettingsFile, $"RepositoryPath={share}\n");
        Package(share, F4);
        var zip = Zip(Path.Combine(share, F4Zip), F4);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(share, StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(zip));
    }

    /// <summary>
    /// A link on the way to the default repository is declined, named as protected and never followed,
    /// so what it points at survives.
    /// </summary>
    [Fact]
    public async Task DeclinesALinkToTheRepository()
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Package(elsewhere, F4);
        var zip = Zip(Path.Combine(elsewhere, F4Zip), F4);
        Directory.CreateDirectory(Path.Combine(_environment.UserProfile, "STM32Cube"));
        Junction.ToDirectory(Repository, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Repository, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(zip));
    }

    /// <summary>
    /// A package folder in the repository that is a link to a package somewhere else is declined and
    /// named as protected, and it pairs with no zip: nothing shows the package is unpacked here. The
    /// zip beside it, the link and what the link points at all survive.
    /// </summary>
    [Fact]
    public async Task APackageFolderThatIsALinkPairsWithNoZip()
    {
        var elsewhere = Package(Path.Combine(_temp.Path, "elsewhere"), F4);
        Directory.CreateDirectory(Repository);
        var link = Path.Combine(Repository, F4);
        Junction.ToDirectory(link, elsewhere);
        var zip = Zip(Path.Combine(Repository, F4Zip), F4);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains(link, StringComparison.Ordinal) && n.Message.Contains("link", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains($"'{F4Zip}'", StringComparison.Ordinal));
        var root = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.False(root.Recognises(new ToolRootChild(F4Zip, ChildKind.File)));
        Assert.False(root.RecognisesFolder(F4));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(elsewhere, "package.xml")));
    }

    /// <summary>A path holding a space may be written in double quotes, which are not part of the folder's name.</summary>
    [Fact]
    public void ReadsAQuotedSetting()
    {
        Write(SettingsFile, 0);
        File.WriteAllText(SettingsFile, "RepositoryPath = \"D:\\ST Tools\\Repository\"\n");

        Assert.Equal(@"D:\ST Tools\Repository", Stm32CubeRepositories.ReadSetting(SettingsFile));
    }

    /// <summary>The value STM32CubeMX's settings hold, as Java's properties writer and a plain writer leave it.</summary>
    [Theory]
    [InlineData(@"C\:\\Users\\testuser\\STM32Cube\\Repository", @"C:\Users\testuser\STM32Cube\Repository")]
    [InlineData(@"C\:/Users/testuser/STM32Cube/Repository/", "C:/Users/testuser/STM32Cube/Repository/")]
    [InlineData(@"C:\Users\testuser\STM32Cube\Repository", @"C:\Users\testuser\STM32Cube\Repository")]
    [InlineData(@"D:\STM32", @"D:\STM32")]
    public void ReadsTheSettingInEitherForm(string written, string read) =>
        Assert.Equal(read, Stm32CubeRepositories.Unescaped(written));
}
