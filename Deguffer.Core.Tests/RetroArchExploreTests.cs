using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1 through the policy Explore actually builds, from both RetroArch rows at once as the app
/// registers them. Explore asks each discovered root on its own and lets any one refuse, so a row that
/// declared only its own way down would refuse everything the other row offers. Asking about each root
/// alone cannot show that.
/// </summary>
public sealed class RetroArchExploreTests : IDisposable
{
    private const string Nes = "Nintendo - Nintendo Entertainment System";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly RetroArchFixture _retroArch;

    public RetroArchExploreTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        // Inside the profile, because Explore acts nowhere else, so a refusal here is RetroArch's own.
        _retroArch = new RetroArchFixture(_environment, Path.Combine(_environment.UserProfile, "Games", "RetroArch-Win64"));
    }

    public void Dispose() => _temp.Dispose();

    private async Task<ExploreActionPolicy> PolicyAsync(FakeProcessInspector? inspector = null)
    {
        var discovery = _retroArch.Discovery(_system);
        inspector ??= FakeProcessInspector.NothingRunning;

        return await ExploreActionPolicy.ForAsync(
            _system,
            _environment,
            new FakeVolumeInventory(),
            [
                new RetroArchDownloadProvider(_environment, new FakeProcessRunner(), inspector, discovery: discovery),
                new RetroArchThumbnailProvider(_environment, new FakeProcessRunner(), inspector, discovery: discovery),
            ]);
    }

    [Fact]
    public async Task EachRowsOffersAreRemovableAndNothingBesideThem()
    {
        _retroArch.Install().Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");
        var database = _retroArch.DatabaseFile("Atari - 2600");
        var boxArt = _retroArch.Pictures(Nes, "Named_Boxarts");
        var preset = RetroArchFixture.WriteFile(Path.Combine(_retroArch.Shaders, "my-crt.slangp"));
        var cursors = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "database", "cursors"));
        var saves = RetroArchFixture.Folder(Path.Combine(_retroArch.Program, "saves"));
        var custom = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, Nes, "Custom"));
        var noPictures = RetroArchFixture.Folder(Path.Combine(_retroArch.Thumbnails, "Sega - Saturn", "Art"));

        var policy = await PolicyAsync();

        foreach (var offered in new[] { slang, database, boxArt })
        {
            Assert.True(policy.MayRemove(offered).IsAllowed, $"{offered}: {policy.MayRemove(offered).Reason}");
        }

        foreach (var kept in new[]
                 {
                     _retroArch.Program, _retroArch.Shaders, _retroArch.Thumbnails, preset, cursors, saves, custom,
                     Path.GetDirectoryName(noPictures)!, _retroArch.Settings,
                 })
        {
            Assert.False(policy.MayRemove(kept).IsAllowed, kept);
        }
    }

    /// <summary>
    /// A folder the settings put outside the program is its own top, and the way down to it must not
    /// become a second root there that recognises nothing.
    /// </summary>
    [Fact]
    public async Task ASetInAFolderOutsideTheProgramIsRemovable()
    {
        var moved = Path.Combine(_environment.UserProfile, "Games", "retro-shaders");
        _retroArch.Install($"video_shader_dir = \"{moved}\"").Declare();
        var slang = _retroArch.ShaderSet("shaders_slang", moved);
        var preset = RetroArchFixture.WriteFile(Path.Combine(moved, "my-crt.slangp"));

        var policy = await PolicyAsync();

        Assert.True(policy.MayRemove(slang).IsAllowed, policy.MayRemove(slang).Reason);
        Assert.False(policy.MayRemove(preset).IsAllowed);
        Assert.False(policy.MayRemove(moved).IsAllowed);
    }

    [Fact]
    public async Task NothingIsRemovableWhileRetroArchRuns()
    {
        _retroArch.Install().Declare();
        var slang = _retroArch.ShaderSet("shaders_slang");
        var boxArt = _retroArch.Pictures(Nes, "Named_Boxarts");

        var policy = await PolicyAsync(new FakeProcessInspector("retroarch"));

        Assert.False(policy.MayRemove(slang).IsAllowed);
        Assert.False(policy.MayRemove(boxArt).IsAllowed);
    }
}
