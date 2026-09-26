using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// RetroArch's thumbnails folder holds one folder for each system, four kinds of picture in each, and
/// the avatars and badges RetroArch keeps beside them. Only the pictures are offered, and a picture the
/// user added cannot be told apart, which is why the row is Tier 3.
/// </summary>
public sealed class RetroArchThumbnailProviderTests : IDisposable
{
    private const string Nes = "Nintendo - Nintendo Entertainment System";

    private const string Snes = "Nintendo - Super Nintendo Entertainment System";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly RetroArchFixture _retroArch;

    public RetroArchThumbnailProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        _retroArch = new RetroArchFixture(_environment, Path.Combine(_temp.Path, "RetroArch-Win64"));
    }

    public void Dispose() => _temp.Dispose();

    private RetroArchThumbnailProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, discovery: _retroArch.Discovery(_system));

    [Fact]
    public void IsTierThreeAndItsPartsAreSeparateDecisions()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.UserData, provider.Tier);
        Assert.Equal(StepGrain.Parts, provider.Grain);
        Assert.False(provider.Tier.IsPreSelectedByDefault());
    }

    [Fact]
    public async Task TakesEachKindOfPictureAndNothingBesideIt()
    {
        _retroArch.Install().Declare();
        var boxArt = _retroArch.Pictures(Nes, "Named_Boxarts");
        var snaps = _retroArch.Pictures(Nes, "Named_Snaps");
        var titles = _retroArch.Pictures(Snes, "Named_Titles");
        var logos = _retroArch.Pictures(Snes, "Named_Logos");

        var somethingElse = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, Nes, "Custom"));
        var looseInSystem = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Thumbnails, Nes, "notes.txt"));
        var looseInThumbnails = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Thumbnails, "readme.txt"));
        var avatars = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "discord", "avatars"));
        var badges = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "cheevos", "badges"));
        var noPictures = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "Sega - Saturn", "Art"));
        var saves = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "saves"));
        var slang = _retroArch.ShaderSet("shaders_slang");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { boxArt, snaps, titles, logos }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.UserData, plan.Tier);

        foreach (var survivor in new[]
                 {
                     _retroArch.Program, _retroArch.Thumbnails, Path.Combine(_retroArch.Thumbnails, Nes), somethingElse,
                     looseInSystem, looseInThumbnails, Path.Combine(_retroArch.Thumbnails, "discord"),
                     Path.Combine(_retroArch.Thumbnails, "cheevos"), noPictures, saves,
                 })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(boxArt));
        Assert.False(Directory.Exists(logos));

        foreach (var folder in new[] { somethingElse, avatars, badges, noPictures, saves, slang, Path.Combine(_retroArch.Thumbnails, Nes) })
        {
            Assert.True(Directory.Exists(folder), folder);
        }

        Assert.True(File.Exists(looseInSystem));
        Assert.True(File.Exists(looseInThumbnails));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A system's pictures are grouped under the system, so a reader keeps the one they added art to.
    /// </summary>
    [Fact]
    public async Task EachSystemsPicturesAreGroupedUnderIt()
    {
        _retroArch.Install().Declare();
        _retroArch.Pictures(Nes, "Named_Boxarts");

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.Equal(Nes, step.Group);
    }

    /// <summary><c>default</c> switches thumbnails off, so RetroArch uses no folder and none is read.</summary>
    [Fact]
    public async Task NothingIsReadWhereTheSettingsSwitchThumbnailsOff()
    {
        _retroArch.Install("thumbnails_directory = \"default\"").Declare();
        var boxArt = _retroArch.Pictures(Nes, "Named_Boxarts");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
        Assert.True(Directory.Exists(boxArt));
    }

    [Fact]
    public async Task FollowsAThumbnailsFolderTheSettingsMove()
    {
        var moved = Path.Combine(_temp.Path, "art");
        _retroArch.Install($"thumbnails_directory = \"{moved}\"").Declare();
        var elsewhere = _retroArch.Pictures(Nes, "Named_Boxarts", moved);
        var stale = _retroArch.Pictures(Nes, "Named_Boxarts");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([elsewhere], plan.TargetedPaths);
        Assert.True(Directory.Exists(stale));
    }

    [Fact]
    public async Task APictureFolderThatIsALinkIsNamedAndNotDeletedThrough()
    {
        _retroArch.Install().Declare();
        var real = _retroArch.Pictures(Nes, "Named_Snaps");
        var elsewhere = RetroArchFixture.Folder(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(_retroArch.Thumbnails, Nes, "Named_Boxarts");
        SymbolicLink.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([real], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, "entry.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2 at every level: the thumbnails folder recognises only a system with pictures in it, and a
    /// system's folder only its pictures. A system with none is refused whole.
    /// </summary>
    [Fact]
    public async Task ExploreIsToldTheWayToThePicturesAndNothingBesideThem()
    {
        _retroArch.Install().Declare();
        _retroArch.Pictures(Nes, "Named_Boxarts");
        RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "Sega - Saturn", "Art"));
        RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "discord", "avatars"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        Assert.Contains(roots, r => r.Path.Equals(_retroArch.Program, StringComparison.OrdinalIgnoreCase) && r.Recognises("thumbnails"));
        Assert.DoesNotContain(roots, r => r.Path.Equals(_retroArch.Program, StringComparison.OrdinalIgnoreCase) && r.Recognises("saves"));

        var thumbnails = roots.Where(r => r.Path.Equals(_retroArch.Thumbnails, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(thumbnails, r => r.Recognises(Nes));
        Assert.DoesNotContain(thumbnails, r => r.Recognises("Sega - Saturn") || r.Recognises("discord"));

        var system = roots.Where(r => r.Path.Equals(Path.Combine(_retroArch.Thumbnails, Nes), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(system, r => r.Recognises("Named_Boxarts"));
        Assert.DoesNotContain(system, r => r.Recognises("Custom"));
    }

    /// <summary>§5.3: RetroArch writes a thumbnail as each game is shown.</summary>
    [Fact]
    public async Task NothingIsOfferedWhileRetroArchRuns()
    {
        _retroArch.Install().Declare();
        var boxArt = _retroArch.Pictures(Nes, "Named_Boxarts");

        var plan = await CreateProvider(new FakeProcessInspector("retroarch")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.True(Directory.Exists(boxArt));
    }
}
