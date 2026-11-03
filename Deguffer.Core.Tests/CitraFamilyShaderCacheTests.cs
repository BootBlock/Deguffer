using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Nintendo 3DS emulators keep their compiled shaders inside the same <c>shaders</c> folder as the
/// user's own post-processing shaders, beside the transferable caches only playing builds. So these
/// are mostly negative tests: only the compiled caches go, by their path from a proven folder.
/// </summary>
public sealed class CitraFamilyShaderCacheTests : IDisposable
{
    private const string Title = "0004000000055D00";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public CitraFamilyShaderCacheTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        Directory.CreateDirectory(_environment.Documents!);
    }

    public void Dispose() => _temp.Dispose();

    private string AzaharRoot => Path.Combine(_environment.RoamingAppData, "Azahar");

    private EmulatorShaderCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            volumes: new FakeVolumeInventory());

    private void Declare(params string[] folders) => Assert.True(new EmulatorFolderStore(_environment).Save(folders));

    private static string WriteFile(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>A data folder of the family, proven by the settings file its Qt front end writes.</summary>
    private static string CitraFamily(string root)
    {
        WriteFile(Path.Combine(root, "config", "qt-config.ini"), 64);
        return root;
    }

    private static string Precompiled(string root) =>
        Path.GetDirectoryName(Path.GetDirectoryName(WriteFile(Path.Combine(root, "shaders", "opengl", "precompiled", "separable", Title + ".bin")))!)!;

    private static bool Holds(IEnumerable<ProtectedPath> paths, string path) =>
        paths.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The compiled OpenGL programs, the per-game Vulkan pipeline caches and the old global one go.
    /// The transferable caches, which only playing builds, the user's own shaders and the saves stay,
    /// and the pipeline folder is not named as left alone while its contents go.
    /// </summary>
    [Fact]
    public async Task TakesAzaharsCompiledCachesAndKeepsTheTransferableOnes()
    {
        CitraFamily(AzaharRoot);
        var shaders = Path.Combine(AzaharRoot, "shaders");
        var precompiled = Precompiled(AzaharRoot);
        var pipeline = WriteFile(Path.Combine(shaders, "vulkan", "pipeline", $"{Title}-10DE2684.bin"));
        var global = WriteFile(Path.Combine(shaders, "vulkan", "10de2684.bin"));

        var glTransferable = WriteFile(Path.Combine(shaders, "opengl", "transferable", Title + ".bin"));
        var vkTransferable = WriteFile(Path.Combine(shaders, "vulkan", "transferable", Title + "_vs.vkch"));
        var loading = WriteFile(Path.Combine(shaders, "vulkan", "pipeline", $"{Title}-loading.flag"));
        var postProcessing = WriteFile(Path.Combine(shaders, "bloom.glsl"));
        var anaglyph = WriteFile(Path.Combine(shaders, "anaglyph", "dubois.glsl"));
        var keys = WriteFile(Path.Combine(AzaharRoot, "sysdata", "keys.txt"));
        var saves = WriteFile(Path.Combine(AzaharRoot, "sdmc", "Nintendo 3DS", "save.bin"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { precompiled, pipeline, global }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["Azahar"], plan.Steps.Select(step => step.Group).Distinct());

        foreach (var survivor in new[] { AzaharRoot, Path.GetDirectoryName(glTransferable)!, Path.GetDirectoryName(vkTransferable)!, loading, Path.Combine(shaders, "anaglyph"), Path.Combine(AzaharRoot, "sdmc") })
        {
            Assert.True(Holds(plan.ProtectedPaths, survivor), $"{survivor} is not asserted to survive");
        }

        Assert.Contains(plan.Notes, n => n.Message.Contains(Path.GetDirectoryName(vkTransferable)!, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(Path.Combine(shaders, "vulkan", "pipeline") + "'", StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(precompiled));
        Assert.False(File.Exists(pipeline));
        Assert.False(File.Exists(global));

        foreach (var file in new[] { glTransferable, vkTransferable, loading, postProcessing, anaglyph, keys, saves })
        {
            Assert.True(File.Exists(file), $"{file} was removed");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Azahar's migration copies the Citra or Lime3DS folder rather than moving it, so the old folders
    /// stay, each with its own cache, offered under its own name.
    /// </summary>
    [Fact]
    public async Task FindsTheFoldersAzaharsMigrationLeftBehind()
    {
        var azahar = Precompiled(CitraFamily(AzaharRoot));
        var lime = Precompiled(CitraFamily(Path.Combine(_environment.RoamingAppData, "Lime3DS")));
        var citra = WriteFile(Path.Combine(CitraFamily(Path.Combine(_environment.RoamingAppData, "Citra")), "shaders", "vulkan", "10de2684.bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { azahar, lime, citra }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "Azahar", "Citra", "Lime3DS" },
            plan.Steps.Select(step => step.Group).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every fork of Citra and of yuzu calls its portable folder <c>user</c> and writes the same
    /// settings file there, so the program beside it decides whose it is.
    /// </summary>
    [Theory]
    [InlineData("azahar.exe", "Azahar")]
    [InlineData("lime3ds-gui.exe", "Lime3DS")]
    [InlineData("citra-qt.exe", "Citra")]
    public async Task APortableFolderIsTheForkWhoseProgramIsBesideIt(string program, string fork)
    {
        var install = Path.Combine(_temp.Path, "emulators", "3ds");
        WriteFile(Path.Combine(install, program), 64);
        var precompiled = Precompiled(CitraFamily(Path.Combine(install, "user")));
        Declare(install);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([precompiled], plan.TargetedPaths);
        Assert.Equal([fork], plan.Steps.Select(step => step.Group).Distinct());
    }

    /// <summary>An Eden <c>user</c> folder holds the same settings file, and is no 3DS emulator's.</summary>
    [Fact]
    public async Task AnEdenUserFolderIsNotRead()
    {
        var install = Path.Combine(_temp.Path, "emulators", "eden");
        WriteFile(Path.Combine(install, "eden.exe"), 64);
        var root = CitraFamily(Path.Combine(install, "user"));
        var lookalike = Precompiled(root);
        Declare(install);

        var plan = await CreateProvider(new FakeProcessInspector("azahar")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Azahar", StringComparison.Ordinal));
        Assert.True(Directory.Exists(lookalike));
    }

    /// <summary>
    /// §5.2 at every level in Explore: the way down through <c>shaders</c> to each compiled cache, and
    /// nothing beside it.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldTheWayToEachCompiledCacheAndNothingBeside()
    {
        CitraFamily(AzaharRoot);
        var shaders = Path.Combine(AzaharRoot, "shaders");
        var precompiled = Precompiled(AzaharRoot);
        var pipeline = WriteFile(Path.Combine(shaders, "vulkan", "pipeline", $"{Title}-10DE2684.bin"));
        var global = WriteFile(Path.Combine(shaders, "vulkan", "10de2684.bin"));
        WriteFile(Path.Combine(shaders, "vulkan", "transferable", Title + "_vs.vkch"));
        WriteFile(Path.Combine(shaders, "opengl", "transferable", Title + ".bin"));
        WriteFile(Path.Combine(shaders, "bloom.glsl"));

        var explore = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await CreateProvider().DiscoverToolRootsAsync());

        Assert.True(explore.MayRemove(precompiled).IsAllowed);
        Assert.True(explore.MayRemove(pipeline).IsAllowed);
        Assert.True(explore.MayRemove(global).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(shaders, "vulkan", "transferable")).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(shaders, "opengl", "transferable")).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(shaders, "vulkan", "pipeline")).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(shaders, "bloom.glsl")).IsAllowed);
        Assert.False(explore.MayRemove(shaders).IsAllowed);
        Assert.False(explore.MayRemove(Path.Combine(AzaharRoot, "sdmc")).IsAllowed);
    }

    /// <summary>§5.3: the emulator writes its caches while a game is played.</summary>
    [Fact]
    public async Task NothingOfAzaharsIsOfferedWhileItRuns()
    {
        var precompiled = Precompiled(CitraFamily(AzaharRoot));
        var citra = Precompiled(CitraFamily(Path.Combine(_environment.RoamingAppData, "Citra")));

        var provider = CreateProvider(new FakeProcessInspector("azahar"));
        var plan = await provider.PlanAsync();

        Assert.Equal([citra], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("Azahar", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(precompiled));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
