using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A browser built from Firefox's source keeps a Firefox profile under a folder named for itself,
/// and the Firefox row reads each through that folder's own <c>profiles.ini</c>. These tests are
/// about the folders being kept apart: a fork's profiles are found under its own two roots and
/// nowhere else, its roaming half is never reached, and nothing is found where no register is.
/// </summary>
public sealed class FirefoxForkProfileTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public FirefoxForkProfileTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private static MozillaInstallation Zen =>
        FirefoxCacheProvider.Installations.Single(installation => installation.Name == "Zen");

    private static MozillaInstallation Firefox =>
        FirefoxCacheProvider.Installations.Single(installation => installation.Name == "Firefox");

    public static TheoryData<string> EveryInstallation() =>
        [.. FirefoxCacheProvider.Installations.Select(installation => installation.Name)];

    private static MozillaInstallation Named(string name) =>
        FirefoxCacheProvider.Installations.Single(installation => installation.Name == name);

    private FirefoxCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    private string RoamingRoot(MozillaInstallation installation) =>
        Path.Combine(_environment.RoamingAppData, installation.Directory);

    private string LocalRoot(MozillaInstallation installation) =>
        Path.Combine(_environment.LocalAppData, installation.Directory);

    /// <summary>One profile's two halves, as the test created them.</summary>
    private sealed record ProfileFixture(string Roaming, string Local);

    /// <summary>
    /// Write a one-profile register into the installation's roaming folder, with the install section
    /// Gecko writes beside it, and create the profile's roaming half.
    /// </summary>
    private ProfileFixture AddProfile(MozillaInstallation installation, string name = "default-release")
    {
        var roaming = Path.Combine(RoamingRoot(installation), "Profiles", name);
        Directory.CreateDirectory(roaming);

        File.WriteAllText(
            Path.Combine(RoamingRoot(installation), MozillaProfileDiscovery.ProfilesFile),
            $"""
            [Install4F96D1932A9F858E]
            Default=Profiles/{name}
            Locked=1

            [Profile0]
            Name={name}
            IsRelative=1
            Path=Profiles/{name}

            [General]
            StartWithLastProfile=1
            Version=2
            """);

        return new ProfileFixture(roaming, Path.Combine(LocalRoot(installation), "Profiles", name));
    }

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[4096]);
        return path;
    }

    private static string[] CreateUserData(ProfileFixture profile)
    {
        string[] names = ["places.sqlite", "key4.db", "logins.json", "cert9.db", "prefs.js"];

        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(profile.Roaming, name), "<REDACTED>");
        }

        return [.. names.Select(name => Path.Combine(profile.Roaming, name))];
    }

    /// <summary>
    /// Every installation the row declares is read under its own folder: a register there yields a
    /// profile whose local half is the matching path under the local root, and its caches are
    /// planned from there.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryInstallation))]
    public async Task EachInstallationIsReadUnderItsOwnFolder(string name)
    {
        var installation = Named(name);
        var fixture = AddProfile(installation);
        var cache = CreateDirectory(Path.Combine(fixture.Local, "cache2"));

        var provider = CreateProvider();
        var profile = Assert.Single(provider.Profiles());

        Assert.Equal(installation, profile.Installation);
        Assert.Equal(fixture.Local, profile.LocalPath, ignoreCase: true);
        Assert.Equal(fixture.Roaming, profile.RoamingPath, ignoreCase: true);
        Assert.True(await provider.IsPresentAsync());
        Assert.Equal([cache], (await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.6 for a fork: executing removes the caches from its local half and leaves its roaming half,
    /// the user data in it by name, its register and an unrecognised sibling of the caches standing.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesAForksCachesAndLeavesItsUserDataStanding()
    {
        var profile = AddProfile(Zen);
        var userData = CreateUserData(profile);
        var cache = CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var unrecognised = CreateDirectory(Path.Combine(profile.Local, "storage"));
        var register = Path.Combine(RoamingRoot(Zen), MozillaProfileDiscovery.ProfilesFile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);

        foreach (var path in userData.Append(unrecognised).Append(register).Append(profile.Roaming))
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(unrecognised), "an unrecognised child of a fork's local half was removed.");
        Assert.All(userData, path => Assert.True(File.Exists(path), $"{path} was removed."));
        Assert.True(File.Exists(register));
    }

    /// <summary>
    /// Explore refuses a fork's roaming half and the folders above its profiles, and allows only the
    /// recognised caches in its local half, as it does for Firefox.
    /// </summary>
    [Theory]
    [InlineData(true, "", false)]                  // the cache folder itself
    [InlineData(true, "cache2", true)]             // a recognised cache
    [InlineData(true, "remote-settings", false)]   // recognised, and deliberately not offered
    [InlineData(true, "storage", false)]           // unrecognised, so left alone
    [InlineData(false, "", false)]                 // the profile itself
    [InlineData(false, "logins.json", false)]      // saved passwords
    [InlineData(false, "key4.db", false)]          // the key that decrypts them
    [InlineData(false, "cache2", false)]           // a cache name in the half that is never touched
    public void AForksProfileIsClassifiedByWhichHalfItIsIn(bool local, string relative, bool allowed)
    {
        AddProfile(Zen);

        var provider = CreateProvider();
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());
        var profile = Assert.Single(provider.Profiles());
        var root = local ? profile.LocalPath : profile.RoamingPath;

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
        Assert.False(policy.MayRemove(RoamingRoot(Zen)).IsAllowed);
        Assert.False(policy.MayRemove(LocalRoot(Zen)).IsAllowed);
    }

    /// <summary>
    /// Firefox and a fork on one machine are both found and kept apart: each profile's caches are
    /// planned from its own local root, and listed under the browser's name as well as the profile's,
    /// because both profiles carry Firefox's default name.
    /// </summary>
    [Fact]
    public async Task FirefoxAndAForkOnOneMachineAreBothFoundAndKeptApart()
    {
        var firefox = AddProfile(Firefox);
        var zen = AddProfile(Zen);
        var firefoxCache = CreateDirectory(Path.Combine(firefox.Local, "cache2"));
        var zenCache = CreateDirectory(Path.Combine(zen.Local, "cache2"));

        var provider = CreateProvider();

        Assert.Equal(
            [(Firefox, firefox.Local), (Zen, zen.Local)],
            provider.Profiles().Select(profile => (profile.Installation, profile.LocalPath)));

        var steps = (await provider.PlanAsync()).Steps
            .OfType<DeleteStep>()
            .ToDictionary(step => step.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, steps.Count);
        Assert.Equal("Firefox — default-release", steps[firefoxCache].Group);
        Assert.Equal("Zen — default-release", steps[zenCache].Group);
    }

    /// <summary>
    /// Identification is the register. A folder with a fork's name and a cache-shaped tree inside it,
    /// but no <c>profiles.ini</c>, is not that fork, and nothing in it is planned.
    /// </summary>
    [Fact]
    public async Task AForksFolderWithNoRegisterYieldsNothing()
    {
        var unregistered = CreateDirectory(Path.Combine(LocalRoot(Zen), "Profiles", "default-release", "cache2"));
        Directory.CreateDirectory(Path.Combine(RoamingRoot(Zen), "Profiles", "default-release"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(provider.Profiles());
        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(unregistered));
    }

    /// <summary>
    /// A fork's register that would not be read is said for that fork, and does not stop Firefox's
    /// profiles being planned, nor let the row read as clear.
    /// </summary>
    [Fact]
    public async Task AForksUnreadableRegisterIsSaidWithoutStoppingFirefox()
    {
        var firefox = AddProfile(Firefox);
        var cache = CreateDirectory(Path.Combine(firefox.Local, "cache2"));
        var register = Path.Combine(RoamingRoot(Zen), MozillaProfileDiscovery.ProfilesFile);
        Directory.CreateDirectory(RoamingRoot(Zen));
        File.WriteAllText(register, "[Profile0]");

        using var held = new FileStream(register, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains("Zen's list of profiles", StringComparison.Ordinal));
    }

    /// <summary>§5.3: each browser in the row is named when it is running, and holds its own caches open.</summary>
    [Theory]
    [MemberData(nameof(EveryInstallation))]
    public async Task EachInstallationsProcessIsNamedWhenItIsRunning(string name)
    {
        var installation = Named(name);
        var fixture = AddProfile(installation);
        CreateDirectory(Path.Combine(fixture.Local, "cache2"));

        var plan = await CreateProvider(new FakeProcessInspector().WithRunning(installation.ProcessName)).PlanAsync();

        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains(installation.ProcessName, StringComparison.OrdinalIgnoreCase));
    }
}
