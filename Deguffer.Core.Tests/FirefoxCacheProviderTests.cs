using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Firefox keeps a profile in two roots, and the whole safety argument for this provider is that it
/// only ever plans against one of them. So these are mostly negative tests: that the roaming half is
/// never reached, that a directory Mozilla's own register does not name is never entered, and that a
/// link anywhere on the derived path stops the pass rather than redirecting it.
/// </summary>
public sealed class FirefoxCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly List<string> _sections = [];

    public FirefoxCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private FirefoxCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string RoamingRoot => Path.Combine(_environment.RoamingAppData, "Mozilla", "Firefox");

    private string LocalRoot => Path.Combine(_environment.LocalAppData, "Mozilla", "Firefox");

    private string RegisterPath => Path.Combine(RoamingRoot, MozillaProfileDiscovery.ProfilesFile);

    /// <summary>One profile, as <c>profiles.ini</c> records it and as it sits on disk.</summary>
    private sealed record ProfileFixture(string Name, string Roaming, string Local);

    /// <summary>
    /// Add a profile to <c>profiles.ini</c> and create its roaming half, the way Firefox does on
    /// first run. The local half is left to the test, because whether it exists is usually the
    /// subject.
    /// </summary>
    private ProfileFixture AddProfile(
        string name = "default-release",
        string? declared = null,
        bool isRelative = true)
    {
        var path = declared ?? $"Profiles/{name}";

        _sections.Add(
            $"[Profile{_sections.Count}]{Environment.NewLine}" +
            $"Name={name}{Environment.NewLine}" +
            $"IsRelative={(isRelative ? "1" : "0")}{Environment.NewLine}" +
            $"Path={path}{Environment.NewLine}");

        WriteRegister();

        var relative = path.Replace('/', Path.DirectorySeparatorChar);
        var roaming = isRelative ? Path.Combine(RoamingRoot, relative) : relative;

        Directory.CreateDirectory(roaming);

        return new ProfileFixture(name, roaming, Path.Combine(LocalRoot, relative));
    }

    /// <summary>
    /// The register as Firefox writes it: an install section carrying a <c>Default=Profiles/…</c>
    /// key of the same shape as a profile's own <c>Path</c>, then the profiles, then the general
    /// preferences. The install section is in every fixture deliberately — reading it as a profile
    /// would enter a directory nobody named.
    /// </summary>
    private void WriteRegister()
    {
        Directory.CreateDirectory(RoamingRoot);

        File.WriteAllText(
            RegisterPath,
            $"[Install4F96D1932A9F858E]{Environment.NewLine}" +
            $"Default=Profiles/somewhere-else{Environment.NewLine}" +
            $"Locked=1{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, _sections) +
            $"{Environment.NewLine}[General]{Environment.NewLine}" +
            $"StartWithLastProfile=1{Environment.NewLine}Version=2{Environment.NewLine}");
    }

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    /// <summary>The five files in the roaming half that the whole two-root split exists to protect.</summary>
    private static string[] CreateUserData(ProfileFixture profile)
    {
        string[] names = ["places.sqlite", "key4.db", "logins.json", "cert9.db", "prefs.js"];

        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(profile.Roaming, name), "<REDACTED>");
        }

        return [.. names.Select(name => Path.Combine(profile.Roaming, name))];
    }

    [Fact]
    public async Task ReportsNotPresentWhenFirefoxHasNoProfileRegister()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.Profiles());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The Unreal lesson the Chromium provider records: a profile existing is not evidence that a
    /// cache inside it does. Firefox writes the roaming half on first run and the local half only
    /// once it has something to put there.
    /// </summary>
    [Fact]
    public async Task AProfileWithNoLocalHalfIsNotPresence()
    {
        var profile = AddProfile();
        CreateUserData(profile);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Single(provider.Profiles());
    }

    [Fact]
    public async Task PlansTheFiveCacheDirectories()
    {
        var profile = AddProfile();

        var caches = new[] { "cache2", "startupCache", "safebrowsing", "thumbnails", "jumpListCache" }
            .Select(name => CreateDirectory(Path.Combine(profile.Local, name)))
            .ToArray();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            caches.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
    }

    /// <summary>
    /// §5.2. Neither half of a profile is a target, and neither is Firefox's own folder under either
    /// application-data root. The register itself is named too: removing it would lose every
    /// profile, and it is the one file that identifies these directories at all.
    /// </summary>
    [Fact]
    public async Task NeverTargetsEitherHalfOfAProfileOrFirefoxsOwnFolders()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var plan = await CreateProvider().PlanAsync();

        foreach (var root in new[] { profile.Local, profile.Roaming, RoamingRoot, LocalRoot, RegisterPath })
        {
            Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, path =>
                Assert.False(IsAtOrUnder(root, path), $"{path} would have taken {root} with it."));

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// The rule the whole provider is built around, tested from the dangerous direction: the roaming
    /// half holds a directory with a recognised cache name, and it must survive anyway. It is not
    /// that Firefox puts <c>cache2</c> there — it does not — but that nothing in this provider may
    /// be reachable from the roaming root, and a table matching on name alone would take it.
    /// </summary>
    [Fact]
    public async Task ARecognisedCacheNameInTheRoamingHalfIsStillNeverTouched()
    {
        var profile = AddProfile();
        var userData = CreateUserData(profile);
        var impostor = CreateDirectory(Path.Combine(profile.Roaming, "cache2"));

        // Something real in the local half, so the pass produces a plan rather than stopping early.
        var real = CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(real, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(impostor, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var file in userData)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(file, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(impostor), "the roaming half was reached.");
        Assert.All(userData, file => Assert.True(File.Exists(file)));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's dangerous direction. Every name here is a real neighbour of the caches in a Firefox
    /// local profile, and none of them is something Deguffer knows how to replace.
    /// </summary>
    /// <param name="name">
    /// <c>shortcutCache</c>, <c>personality-provider</c> and <c>settings</c> were observed beside
    /// the five in a live Firefox profile. <c>shortcutCache</c> is the one that matters most: it
    /// carries the word "Cache" and is still not on the list, so a rule that matched on the name
    /// rather than on the table would take it.
    /// </param>
    [Theory]
    [InlineData("shortcutCache")]
    [InlineData("personality-provider")]
    [InlineData("settings")]
    [InlineData("storage")]
    [InlineData("minidumps")]
    [InlineData("datareporting")]
    [InlineData("SuperCache")]
    public async Task AnUnrecognisedSiblingIsTier4AndIsAssertedToSurvive(string name)
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var sibling = CreateDirectory(Path.Combine(profile.Local, name));

        Assert.Equal(SafetyTier.DoNotTouch, FirefoxCacheProvider.Children.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        // Not merely absent from the plan — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"{name} was removed alongside the caches.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The decision the issue behind this provider left open. <c>remote-settings</c> is four fifths
    /// of the local profile, so leaving it out silently would leave the user unable to account for
    /// the space — and offering it would offer a re-download whose cost nobody has established. It
    /// is measured, named with its size, and not planned.
    /// </summary>
    [Fact]
    public async Task TheSynchronisedDataIsMeasuredAndReportedRatherThanOffered()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var synchronised = CreateDirectory(Path.Combine(profile.Local, "remote-settings"), bytes: 32768);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            FirefoxCacheProvider.Children.Classify("remote-settings").Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(synchronised, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(synchronised, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        // Measured, not merely mentioned: the note carries the size, so a figure of zero would mean
        // the directory was never scanned.
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(synchronised, StringComparison.Ordinal) &&
            n.Message.Contains("32 KB", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(synchronised));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The synchronised dataset is reported whether or not there is a cache to offer beside it. It
    /// was 1.5 GB of a 1.9 GB profile on the machine this was measured on, so a profile holding it
    /// and nothing else is exactly the case where a user asks where the space went — and exactly the
    /// case a plan that short-circuits on "no targets" says nothing about.
    /// </summary>
    [Fact]
    public async Task TheSynchronisedDataIsReportedEvenWhenThereIsNoCacheToOffer()
    {
        var profile = AddProfile();
        var synchronised = CreateDirectory(Path.Combine(profile.Local, "remote-settings"), bytes: 32768);

        var provider = CreateProvider();

        // The planner never asks an absent provider for a plan, so a presence probe that only
        // knows the five offered names makes every sentence below unreachable.
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(synchronised, StringComparison.Ordinal) &&
            n.Message.Contains("32 KB", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same short-circuit, from the direction that makes a false claim rather than an incomplete
    /// one: one profile is stored somewhere Deguffer will not examine, and the other has no cache.
    /// Dropping the note leaves the row reading as clear about a profile nobody looked at.
    /// </summary>
    [Fact]
    public async Task AProfileStoredElsewhereIsStillReportedWhenTheOtherProfileHasNoCache()
    {
        var relative = AddProfile();
        CreateDirectory(Path.Combine(relative.Local, "storage"));

        AddProfile("hand-placed", declared: _temp.CreateDirectory("moved-profile"), isRelative: false);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("hand-placed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule where there is nothing else at all: every profile Firefox has is stored
    /// somewhere Deguffer will not examine. The provider must still report itself present, because
    /// the planner never asks an absent provider for a plan and the row would then read "Not
    /// installed" about a Firefox that is installed.
    /// </summary>
    [Fact]
    public async Task AFirefoxWhoseOnlyProfileIsStoredElsewhereIsPresentRatherThanAbsent()
    {
        var elsewhere = _temp.CreateDirectory("moved-profile");
        AddProfile("hand-placed", declared: elsewhere, isRelative: false);
        CreateDirectory(Path.Combine(elsewhere, "cache2"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("hand-placed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Identification is a <c>[ProfileN]</c> section's own relative <c>Path</c> and nothing else. A
    /// directory sitting beside a real profile, holding a directory called <c>cache2</c>, is not a
    /// profile — the same rule as the Chromium provider's "a cache name is not licence to look inside
    /// a folder", arriving through Mozilla's own register instead of through a marker file.
    ///
    /// <para>The subject is the directory the fixture's install section points at, because that is
    /// the realistic way to get this wrong: <c>[Install…]</c> carries <c>Default=Profiles/…</c>, a
    /// value of exactly the shape a profile's <c>Path</c> has.</para>
    ///
    /// <para><b>Three independent guards hold this, and it takes all three to break it.</b> The
    /// section has to match <c>ProfileN</c>, the key has to be <c>Path</c>, and <c>IsRelative</c> has
    /// to say 1. Mutating any one leaves the other two refusing the directory, so a mutation pass
    /// over a single guard reads as a test that proves nothing. It is defence in depth rather than a
    /// weak assertion, and it is written down so that nobody removes a guard on the strength of one
    /// green run.</para>
    /// </summary>
    [Fact]
    public async Task ADirectoryTheRegisterDoesNotNameAsAProfileIsNeverLookedInside()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var stranger = CreateDirectory(Path.Combine(LocalRoot, "Profiles", "somewhere-else", "cache2"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(stranger), "a directory Mozilla's register does not name was entered.");
    }

    /// <summary>
    /// A profile the user moved elsewhere by hand. Its two halves are one directory, so its caches
    /// sit among <c>places.sqlite</c> and <c>logins.json</c> rather than apart from them, and this
    /// provider's whole argument stops applying. It is reported rather than silently skipped: a plan
    /// that said nothing would let the user read "already clear" about a folder never looked at.
    /// </summary>
    [Fact]
    public async Task AProfileStoredElsewhereIsReportedAndNeverExamined()
    {
        var relative = AddProfile();
        CreateDirectory(Path.Combine(relative.Local, "cache2"));

        var elsewhere = _temp.CreateDirectory("moved-profile");
        AddProfile("hand-placed", declared: elsewhere, isRelative: false);
        var cache = CreateDirectory(Path.Combine(elsewhere, "cache2"));

        var provider = CreateProvider();

        Assert.Single(provider.Profiles());

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("hand-placed", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(cache));
    }

    /// <summary>
    /// The register is a text file on disk, so a path in it is a claim rather than a fact. A
    /// relative entry that climbs back out of Firefox's own folder would put every later step —
    /// the enumeration, the classification, the deletion — on a directory nobody chose.
    /// </summary>
    /// <param name="declared">
    /// Five levels up from <c>%APPDATA%\Mozilla\Firefox</c> is the scratch root itself. Both lengths
    /// are here deliberately: the first resolves to a path <em>shorter</em> than Firefox's own
    /// folder, so a rule that only compared lengths would refuse it and pass this test while leaving
    /// the second — which is longer, and just as far outside — accepted.
    /// </param>
    [Theory]
    [InlineData("../../../../../Documents")]
    [InlineData("../../../../../Documents/a-deliberately-long-directory-name-to-outlength-the-root")]
    public async Task ARelativePathThatClimbsOutOfFirefoxsFolderIsRefused(string declared)
    {
        AddProfile("escaping", declared: declared);

        var escaped = Path.GetFullPath(
            Path.Combine(RoamingRoot, declared.Replace('/', Path.DirectorySeparatorChar)));
        var documents = CreateDirectory(Path.Combine(escaped, "cache2"));

        var provider = CreateProvider();

        Assert.Empty(provider.Profiles());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        // Refused, and said so. A silent skip would let the row read as clear about a folder that
        // was never looked at.
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("escaping", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(documents));
    }

    /// <summary>
    /// A register that will not be read leaves this provider with no profiles and nothing to say,
    /// which the planner renders as "Not installed" — a claim about a file Deguffer never read.
    ///
    /// The refusal is a real one rather than a fake: the file is held open exclusively, which is
    /// what <see cref="File.ReadAllLines(string)"/> actually meets on a live machine.
    /// </summary>
    [Fact]
    public async Task ARegisterThatWillNotBeReadIsSaidSoRatherThanReportedAsAbsent()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        using var held = new FileStream(RegisterPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains(RegisterPath, StringComparison.Ordinal));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The local profile is reached by name rather than by an enumeration that filters links, and
    /// every segment of that name was synthesised from a text file plus two constants. A junction at
    /// any one of them redirects the deletion while every §5.6 survivor named in the roaming half
    /// resolves independently and passes — the vacuous negative.
    /// </summary>
    [Theory]
    [InlineData("Mozilla")]
    [InlineData(@"Mozilla\Firefox")]
    [InlineData(@"Mozilla\Firefox\Profiles\default-release")]
    public async Task AJunctionAnywhereOnThePathToTheCacheIsNeverLookedThrough(string relative)
    {
        var profile = AddProfile();

        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "cache2"));

        var link = Path.Combine(_environment.LocalAppData, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(
            Path.Combine(profile.Local, "cache2"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(bystander, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a link and deleted the far side.");
    }

    /// <summary>
    /// A junctioned cache is a child the user can see, so a plan that neither offers it nor mentions
    /// it disagrees with the folder. Dropping it silently would also make the row read as clear,
    /// since presence resolves through the link.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsNamedRatherThanDroppedSilently()
    {
        var profile = AddProfile();
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        Directory.CreateDirectory(profile.Local);
        Directory.CreateSymbolicLink(Path.Combine(profile.Local, "cache2"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("cache2", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")), "a junctioned cache was deleted through.");
    }

    /// <summary>
    /// A local profile that will not be listed is one Deguffer never examined, and reporting that as
    /// "no cache" states something nobody established — the presence probe reached the cache by full
    /// name through the same directory, because listing and traversing are separate rights.
    /// </summary>
    [Fact]
    public async Task ALocalProfileThatWillNotBeListedIsSaidSoRatherThanReportedAsHavingNoCache()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        using var denied = new DeniedDirectory(profile.Local);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(profile.Local, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n =>
            n.Message.Contains("keeping a cache on disk", StringComparison.Ordinal));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// §7's per-step selection is what gives per-profile control. A user who keeps a work profile
    /// and a personal one can clear one and leave the other.
    /// </summary>
    [Fact]
    public async Task EachProfileGetsItsOwnStepsSoOneCanBeKept()
    {
        var work = AddProfile("work");
        var personal = AddProfile("personal");

        var workCache = CreateDirectory(Path.Combine(work.Local, "cache2"));
        var personalCache = CreateDirectory(Path.Combine(personal.Local, "cache2"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { workCache, personalCache }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        var narrowed = plan.NarrowedTo(
            [.. plan.Steps.Where(s => s is DeleteDirectoryStep d
                && d.Path.Equals(workCache, StringComparison.OrdinalIgnoreCase))]);

        var result = await provider.ExecuteAsync(narrowed);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(workCache));
        Assert.True(Directory.Exists(personalCache), "the profile the user kept was cleared as well.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7 scopes the age column to per-workspace and per-project data. Each of these is one whole
    /// cache for one profile, so a timestamp on it would be a number with nothing to mean.
    /// </summary>
    [Fact]
    public async Task NoStepCarriesAnAgeBecauseTheseAreWholeCaches()
    {
        var profile = AddProfile();
        CreateDirectory(Path.Combine(profile.Local, "cache2"));
        CreateDirectory(Path.Combine(profile.Local, "startupCache"));

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
    }

    /// <summary>
    /// The table is designed to grow, and a child declared above Tier 1 would be planned under this
    /// provider's Tier 1 sentence and pre-selected, because a plan carries the provider's tier rather
    /// than the child's.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var name in FirefoxCacheProvider.Children.DisposableNames)
        {
            Assert.Equal(provider.Tier, FirefoxCacheProvider.Children.Classify(name).Tier);
        }
    }

    /// <summary>
    /// The five names, and only the five. A sixth appearing without the reasoning that belongs to it
    /// is how an allow-list stops being one — and <c>remote-settings</c> must stay out of this list
    /// while staying in the table, because it is declared precisely so that it is not offered.
    /// </summary>
    [Fact]
    public void TheTableOffersTheFiveCacheNamesAndNoOthers()
    {
        Assert.Equal(
            ["cache2", "jumpListCache", "safebrowsing", "startupCache", "thumbnails"],
            FirefoxCacheProvider.Children.DisposableNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheProfileStanding()
    {
        var profile = AddProfile();
        var userData = CreateUserData(profile);

        var cache = CreateDirectory(Path.Combine(profile.Local, "cache2"));
        var startup = CreateDirectory(Path.Combine(profile.Local, "startupCache"));
        var storage = CreateDirectory(Path.Combine(profile.Local, "storage"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(cache));
        Assert.False(Directory.Exists(startup));

        Assert.True(Directory.Exists(profile.Local));
        Assert.True(Directory.Exists(profile.Roaming));
        Assert.True(Directory.Exists(storage));
        Assert.All(userData, file => Assert.True(File.Exists(file)));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6, from the direction that loses data: the roaming half is what must survive.</summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfTheRoamingProfileVanished()
    {
        var profile = AddProfile();
        CreateUserData(profile);
        CreateDirectory(Path.Combine(profile.Local, "cache2"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        Directory.Delete(profile.Roaming, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(
            verification.Failures, c => c.Path.Equals(profile.Roaming, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.3. A Firefox <c>cache2</c> is a wide store of small entry files under a folder already
    /// several segments deep. A smoke test, and knowingly so: .NET prefixes long paths itself before
    /// calling Win32, so what proves Core applies the prefix is
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c>. This one
    /// earns its place as a crash guard over a deep tree.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        var profile = AddProfile();
        var cache = Path.Combine(profile.Local, "cache2");

        var deep = cache;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('e', 40));
        }

        var entry = Path.Combine(deep, "entry.bin");
        Assert.True(entry.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(entry), new byte[4096]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "A Firefox cache past MAX_PATH was measured as empty.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(entry), "An entry past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(cache));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
