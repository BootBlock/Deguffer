using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Thunderbird shares Firefox's profile layout, and <see cref="FirefoxCacheProviderTests"/> covers
/// that shared machinery: the register, the links on the derived path, profiles kept elsewhere.
/// These cover what is Thunderbird's own: its roots, the three names a measured profile held, the
/// mail in the roaming half, and that neither application's row reaches the other's profiles.
/// </summary>
public sealed class ThunderbirdCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ThunderbirdCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private ThunderbirdCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    /// <summary>One profile's two halves.</summary>
    private sealed record ProfileFixture(string Roaming, string Local);

    /// <summary>
    /// A register as Thunderbird 159 wrote it on first run, with its install section, and the
    /// roaming half of the one profile it names.
    /// </summary>
    private ProfileFixture AddProfile(string application = "Thunderbird", string name = "default-release")
    {
        var roamingRoot = Path.Combine(_environment.RoamingAppData, application);
        var relative = Path.Combine("Profiles", $"x7k2abcd.{name}");

        Directory.CreateDirectory(roamingRoot);
        File.WriteAllText(
            Path.Combine(roamingRoot, MozillaProfileDiscovery.ProfilesFile),
            $"""
            [General]
            StartWithLastProfile=1
            Version=2

            [Profile0]
            Name={name}
            IsRelative=1
            Path=Profiles/x7k2abcd.{name}

            [Install2F0C2FA1A2E8B1D0]
            Default=Profiles/x7k2abcd.{name}
            Locked=1
            """);

        var roaming = Path.Combine(roamingRoot, relative);
        Directory.CreateDirectory(roaming);

        return new ProfileFixture(roaming, Path.Combine(_environment.LocalAppData, application, relative));
    }

    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    /// <summary>
    /// The mail, address books, keys and accounts in the roaming half: what the whole two-root split
    /// exists to keep out of reach.
    /// </summary>
    private static string[] CreateMail(ProfileFixture profile)
    {
        string[] files =
        [
            "abook.sqlite", "history.sqlite", "global-messages-db.sqlite", "key4.db", "logins.json",
            "secring.gpg", "prefs.js",
        ];

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(profile.Roaming, file), "<REDACTED>");
        }

        string[] folders = ["Mail", "ImapMail", "calendar-data"];

        foreach (var folder in folders)
        {
            CreateDirectory(Path.Combine(profile.Roaming, folder));
        }

        return [.. files.Concat(folders).Select(name => Path.Combine(profile.Roaming, name))];
    }

    [Fact]
    public async Task ReportsNotPresentWhenThunderbirdHasNoProfileRegister()
    {
        // Firefox's register is no evidence of Thunderbird.
        var firefox = AddProfile(@"Mozilla\Firefox");
        CreateDirectory(Path.Combine(firefox.Local, "cache2"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Profiles());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>The three directories a measured Thunderbird profile held in its local half.</summary>
    [Fact]
    public async Task PlansTheThreeCacheDirectoriesUnderThunderbirdsOwnRoots()
    {
        var profile = AddProfile();

        var caches = new[] { "cache2", "startupCache", "safebrowsing" }
            .Select(name => CreateDirectory(Path.Combine(profile.Local, name)))
            .ToArray();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            caches.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("thunderbird", plan.ProviderId);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.All(plan.Steps, step => Assert.Equal("default-release", step.Group));
    }

    [Fact]
    public void TheTableOffersTheThreeMeasuredNamesAndNoOthers() =>
        Assert.Equal(
            ["cache2", "safebrowsing", "startupCache"],
            ThunderbirdCacheProvider.Children.DisposableNames.Order(StringComparer.Ordinal));

    /// <summary>
    /// §5.2's unrecognised case. Firefox's other caches were not in the measured Thunderbird profile,
    /// so a directory by one of their names there is something nobody classified, and Tier 4 like
    /// any other neighbour.
    /// </summary>
    [Theory]
    [InlineData("thumbnails")]
    [InlineData("jumpListCache")]
    [InlineData("remote-settings")]
    [InlineData("OfflineCache")]
    [InlineData("storage")]
    public async Task ANameThunderbirdWasNotMeasuredKeepingIsTier4AndSurvives(string name)
    {
        var profile = AddProfile();
        var cache = CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var sibling = CreateDirectory(Path.Combine(profile.Local, name));

        Assert.Equal(SafetyTier.DoNotTouch, ThunderbirdCacheProvider.Children.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(sibling), $"{name} was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6 from the direction that loses mail: every piece of it is named as a survivor, a cache
    /// name in the roaming half is never a target, and all of it is still there afterwards.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheMailStanding()
    {
        var profile = AddProfile();
        var mail = CreateMail(profile);
        var impostor = CreateDirectory(Path.Combine(profile.Roaming, "cache2"));
        var cache = CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var startup = CreateDirectory(Path.Combine(profile.Local, "startupCache"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(impostor, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var path in mail.Append(profile.Roaming).Append(profile.Local))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache));
        Assert.False(Directory.Exists(startup));
        Assert.True(Directory.Exists(impostor), "the roaming half was reached.");
        Assert.All(mail, path => Assert.True(Path.Exists(path), $"{path} did not survive."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheMailVanished()
    {
        var profile = AddProfile();
        CreateMail(profile);
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var mail = Path.Combine(profile.Roaming, "Mail");
        Directory.Delete(mail, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(mail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Firefox and Thunderbird keep the same names in the same shape under different folders, and
    /// each row reaches only its own application's profiles.
    /// </summary>
    [Fact]
    public async Task NeitherRowReachesTheOtherApplicationsProfiles()
    {
        var thunderbird = AddProfile();
        var firefox = AddProfile(@"Mozilla\Firefox");
        var mailCache = CreateDirectory(Path.Combine(thunderbird.Local, "cache2"));
        var webCache = CreateDirectory(Path.Combine(firefox.Local, "cache2"));

        var thunderbirdPlan = await CreateProvider().PlanAsync();
        var firefoxPlan = await new FirefoxCacheProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning).PlanAsync();

        Assert.Equal([mailCache], thunderbirdPlan.TargetedPaths);
        Assert.Equal([webCache], firefoxPlan.TargetedPaths);
    }

    /// <summary>§5.3: a running Thunderbird holds its cache open, and the plan says so.</summary>
    [Fact]
    public async Task WarnsWhenThunderbirdIsRunning()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var plan = await CreateProvider(FakeProcessInspector.NothingRunning.WithRunning("thunderbird")).PlanAsync();

        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>§7.4: the places named without planning hold every cache the plan cleans.</summary>
    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        var profile = AddProfile();
        CreateMail(profile);
        CreateDirectory(Path.Combine(profile.Local, "cache2"));
        CreateDirectory(Path.Combine(profile.Local, "startupCache"));
        CreateDirectory(Path.Combine(profile.Local, "safebrowsing"));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }
}
