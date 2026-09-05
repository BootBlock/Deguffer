using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1's refusals, asserted without a WinUI host — which is the whole reason the decision is a
/// Core type rather than a disabled context-menu item.
///
/// <para>Everything here runs against a synthetic Windows directory, synthetic program directories
/// and a synthetic profile. That is not a convenience: the rule that matters is that Explore never
/// reaches <c>C:\Windows</c>, and it has to be demonstrable on a machine where nobody may delete
/// anything in there.</para>
/// </summary>
public sealed class ExploreActionPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;

    public ExploreActionPolicyTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheWindowsDirectoryAndEverythingInItIsRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.WindowsDirectory).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.WindowsDirectory, "System32")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.WindowsDirectory, "System32", "drivers", "etc")).IsAllowed);
    }

    /// <summary>
    /// §9's exclusions inside the Windows directory, by name. They are covered by the rule above,
    /// and naming them anyway is the point: §9 is enforced by nothing except not reaching those
    /// paths, so an assertion that says "we did not reach them" is what turns that into evidence.
    /// </summary>
    [Theory]
    [InlineData("WinSxS")]
    [InlineData("Installer")]
    public void TheSection9ExclusionsInsideWindowsAreRefused(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_system.WindowsDirectory, name)).IsAllowed);
    }

    /// <summary>
    /// §9's other two, which sit under a different root and so are named separately. Both are
    /// installer caches with the same failure mode, and both are among the largest directories on a
    /// developer's machine — which is exactly the shape of thing a size picture invites somebody to
    /// act on, so the refusal is worth an assertion of its own rather than inheriting one.
    /// </summary>
    [Theory]
    [InlineData("Package Cache")]
    [InlineData(@"Microsoft\VisualStudio\Packages")]
    public void TheInstallerCachesUnderProgramDataAreRefused(string relativePath)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_system.ProgramData, relativePath)).IsAllowed);
    }

    /// <summary>
    /// Both program directories. A rule that knew only the 64-bit one would allow half the
    /// installed software on the machine, which is the shape of hole nobody notices.
    /// </summary>
    [Fact]
    public void BothProgramDirectoriesAreRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.ProgramFiles).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramFiles, "Some Vendor", "bin")).IsAllowed);
        Assert.False(policy.MayRemove(_system.ProgramFilesX86).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramFilesX86, "Some Vendor")).IsAllowed);
    }

    [Fact]
    public void MachineWideApplicationDataIsRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.ProgramData).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramData, "Some Vendor")).IsAllowed);
    }

    [Fact]
    public void AWholeDriveIsRefused()
    {
        Assert.False(Policy().MayRemove(_temp.Path).IsAllowed);
        Assert.False(Policy().MayRemove(@"C:\").IsAllowed);
    }

    /// <summary>
    /// The three entries that read as one rule: the profile is not a thing to remove, what the user
    /// keeps inside it is ordinary, and another account's profile is neither.
    /// </summary>
    [Fact]
    public void TheProfileItselfIsRefusedWhileWhatIsInsideItIsNot()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_environment.UserProfile).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(_environment.UserProfile, "Downloads")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(_environment.UserProfile, "Downloads", "big.iso")).IsAllowed);
    }

    [Fact]
    public void AnotherAccountsProfileIsRefused()
    {
        var users = Path.GetDirectoryName(_environment.UserProfile)!;

        Assert.False(Policy().MayRemove(Path.Combine(users, "someone-else")).IsAllowed);
        Assert.False(Policy().MayRemove(Path.Combine(users, "someone-else", "Documents")).IsAllowed);
    }

    /// <summary>
    /// What Windows reserves at the top of a volume, on a drive the policy was never told about.
    ///
    /// <para>The drive is the point. These were once a table built from
    /// <see cref="Deguffer.Core.Safety.IVolumeInventory"/>, which is a snapshot — so a volume mounted
    /// after the page opened was scannable with its paging file and its restore points unprotected.
    /// Reading it from the path needs no inventory, and this asserts it against a letter no fake ever
    /// mentioned.</para>
    /// </summary>
    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("$Recycle.Bin")]
    [InlineData("pagefile.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("SYSTEM VOLUME INFORMATION")]
    public void WhatWindowsKeepsAtAVolumeRootIsRefusedOnAnyDrive(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_temp.Path, name)).IsAllowed);
        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\", name)).IsAllowed);
    }

    /// <summary>
    /// The same names one level down are ordinary. A folder somebody called
    /// <c>System Volume Information</c> inside their own Documents is theirs, and the rule is about
    /// the reserved place rather than the word.
    /// </summary>
    [Fact]
    public void TheSameNameInsideAFolderIsNotReserved()
    {
        Assert.True(Policy()
            .MayRemove(Path.Combine(_environment.UserProfile, "Documents", "System Volume Information"))
            .IsAllowed);
    }

    /// <summary>
    /// NTFS's own records, which §7.1 puts out of reach: they are live filesystem state, so the tier
    /// model calls them Tier 4 and Explore refuses them without getting to decide otherwise.
    ///
    /// <para>They are here because §5.5's file-table route draws them. A directory walk never sees
    /// these names, so before that route existed nothing could put one in front of a user; reading
    /// the table directly puts <c>$MFT</c> at the top of a scanned drive at several hundred
    /// megabytes.</para>
    /// </summary>
    [Theory]
    [InlineData("$MFT")]
    [InlineData("$MFTMirr")]
    [InlineData("$LogFile")]
    [InlineData("$Volume")]
    [InlineData("$AttrDef")]
    [InlineData("$Bitmap")]
    [InlineData("$Boot")]
    [InlineData("$BadClus")]
    [InlineData("$Secure")]
    [InlineData("$UpCase")]
    [InlineData("$Extend")]
    [InlineData("$mft")]
    public void WhatNtfsReservesIsRefusedOnAnyDrive(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_temp.Path, name)).IsAllowed);
        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\", name)).IsAllowed);
    }

    /// <summary>
    /// And everything under <c>$Extend</c>, where NTFS keeps the features that are not part of the
    /// core on-disk format. The change journal is the one that grows, so it is the one a size
    /// picture is most likely to surface — and it is a level below the root, which is why the rule
    /// asks about the first segment rather than about a direct child.
    ///
    /// <para>On a drive the region table says nothing about, so what is being measured is this rule
    /// and not another one. The synthetic profile lives under the temp directory, and everything
    /// beside it there is already refused as another account's.</para>
    /// </summary>
    [Theory]
    [InlineData(@"$Extend\$UsnJrnl")]
    [InlineData(@"$Extend\$RmMetadata")]
    [InlineData(@"$Extend\$RmMetadata\$Tops")]
    public void EverythingBelowTheExtendDirectoryIsRefused(string relative)
    {
        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\", relative)).IsAllowed);
    }

    /// <summary>
    /// The negative half, and the half that matters. The rule is about the names NTFS reserves at a
    /// volume root, so it must not reach a folder that merely starts with a dollar, nor an upgrade
    /// leftover somebody may legitimately want gone — refusing those would take a capability away
    /// rather than add a protection.
    ///
    /// <para>Asserted on a drive the region table says nothing about, so what is being measured is
    /// this rule and not another one. The synthetic profile lives under the temp directory, and
    /// everything beside it there is refused as another account's.</para>
    /// </summary>
    [Theory]
    [InlineData("$WinREAgent")]
    [InlineData("$Windows.~BT")]
    [InlineData("$GetCurrent")]
    [InlineData("$MFTBackup")]
    [InlineData("MFT")]
    public void ANameNtfsDoesNotReserveIsNotRefusedForThatReason(string name)
    {
        Assert.True(Policy().MayRemove(Path.Combine(@"Q:\", name)).IsAllowed);
    }

    /// <summary>
    /// And not one level down either. A folder somebody called <c>$MFT</c> inside their own
    /// documents is theirs, and the rule is about the reserved place rather than the word.
    /// </summary>
    [Fact]
    public void AReservedNameInsideAFolderIsOrdinary()
    {
        Assert.True(Policy()
            .MayRemove(Path.Combine(_environment.UserProfile, "Documents", "$MFT"))
            .IsAllowed);
    }

    /// <summary>
    /// A region whose path will not resolve is dropped rather than kept with the value it arrived
    /// with. An empty one prefix-matches every UNC path, so admitting it would refuse a whole network
    /// share with a sentence naming no directory at all — and <c>%ProgramFiles(x86)%</c> is genuinely
    /// empty on a 32-bit Windows.
    /// </summary>
    [Fact]
    public void ARegionThatNamesNothingProtectsNothing()
    {
        var policy = new ExploreActionPolicy(
            [ProtectedRegion.Refusing(string.Empty, RegionScope.PathAndBelow, "Nowhere.")],
            []);

        Assert.True(policy.MayRemove(@"\\server\share\folder").IsAllowed);
        Assert.True(policy.MayRemove(@"C:\anywhere\at\all").IsAllowed);
    }

    [Fact]
    public void AToolRootIsNeverRemoved()
    {
        Assert.False(Policy(Gradle()).MayRemove(GradleRoot).IsAllowed);
    }

    /// <summary>
    /// §5.2's unrecognised case, which is the dangerous direction: an unknown thing must not be
    /// treated as safe. <c>gradle.properties</c> is the example §7.1 chose, and it may hold signing
    /// keys and credentials.
    /// </summary>
    [Theory]
    [InlineData("gradle.properties")]
    [InlineData("init.d")]
    [InlineData(@"init.d\company.gradle")]
    [InlineData("something-a-later-gradle-added")]
    public void AnUnrecognisedChildOfAToolRootIsRefused(string relative)
    {
        var verdict = Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, relative));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("not something Deguffer recognises", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first segment below the root decides, not the leaf. Asking about the leaf instead would
    /// refuse <c>caches\modules-2</c> and allow <c>init.d\company.gradle</c>, which is exactly
    /// backwards.
    /// </summary>
    [Theory]
    [InlineData("caches")]
    [InlineData(@"caches\modules-2")]
    [InlineData(@"caches\modules-2\files-2.1\org.example")]
    [InlineData("wrapper")]
    public void ARecognisedChildOfAToolRootTakesWhatIsUnderItToo(string relative)
    {
        Assert.True(Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, relative)).IsAllowed);
    }

    /// <summary>
    /// The profile is permitted below, and <c>.gradle</c> sits inside it. The permitting entry ends
    /// the structural table's search and not the §5.2 check that follows it — get that ordering
    /// wrong and every tool root in the user's own profile stops being protected, which is all of
    /// them.
    /// </summary>
    [Fact]
    public void BeingInsideThePermittedProfileDoesNotOverrideSection52()
    {
        Assert.True(LongPath.Contains(_environment.UserProfile, GradleRoot));
        Assert.False(Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, "gradle.properties")).IsAllowed);
    }

    /// <summary>
    /// §7.1: "A path Explore does not recognise is unclassified, not safe." Most of a drive is in
    /// this state, and what the user is told about it must not be the word the tier model reserves
    /// for a thing a provider examined.
    /// </summary>
    [Fact]
    public void AnUnknownPathIsAllowedAndIsNeverDescribedAsSafe()
    {
        var verdict = Policy().MayRemove(Path.Combine(_environment.UserProfile, "Videos", "holiday.mp4"));

        Assert.True(verdict.IsAllowed);
        Assert.DoesNotContain("safe", verdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not classified", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path the rules cannot be applied to is refused rather than waved through. Every comparison
    /// in the policy is a prefix match on text, so a value that will not normalise would walk past
    /// the whole table.
    /// </summary>
    [Theory]
    [InlineData("not-a-full-path")]
    [InlineData(@"..\somewhere")]
    [InlineData("")]
    public void APathThatWillNotNormaliseIsRefused(string path)
    {
        Assert.False(Policy().MayRemove(path).IsAllowed);
    }

    /// <summary>
    /// A provider whose caches sit below its root declares a root per level, and the <em>innermost</em>
    /// one decides.
    ///
    /// <para>Cargo is the case. <c>registry</c> is Tier 4 at the home's level, precisely so that only
    /// what is named inside it goes — so asking the outer root about <c>registry\cache</c> refuses
    /// the one directory the provider removes. Asking the level that was written about that
    /// directory is the whole point of declaring one per level.</para>
    /// </summary>
    [Theory]
    [InlineData("", false)]                        // the home itself
    [InlineData("registry", false)]                // a Tier 4 container
    [InlineData("git", false)]
    [InlineData("bin", false)]                     // installed executables
    [InlineData(@"registry\cache", true)]          // what Cargo re-downloads
    [InlineData(@"registry\src", true)]
    [InlineData(@"registry\cache\github.com-1", true)]
    [InlineData(@"registry\index", false)]         // metadata Deguffer leaves
    [InlineData(@"git\checkouts", true)]
    [InlineData(@"git\db", false)]                 // the only copy of that history
    [InlineData("credentials.toml", false)]        // unrecognised, so left alone
    public void TheInnermostToolRootDecidesANestedPath(string relative, bool allowed)
    {
        var provider = new CargoCacheProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots);
        var home = Path.Combine(_environment.UserProfile, ".cargo");

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? home : Path.Combine(home, relative)).IsAllowed);
    }

    /// <summary>
    /// The same rule where the outer level recognises <em>nothing</em>. The Azure Functions tooling
    /// keeps its feed and its tag records directly beside <c>Releases</c>, and both are how it knows
    /// which releases it holds — so the outer declaration allows no child at all, and only the level
    /// written about the releases lets one go.
    /// </summary>
    [Theory]
    [InlineData("", false)]                              // the tooling's own folder
    [InlineData("Tags", false)]                          // which release each Functions line uses
    [InlineData(@"Tags\v4", false)]
    [InlineData("feed-v2167102.json", false)]            // what it already has
    [InlineData("Releases", false)]                      // the folder, never a target
    [InlineData(@"Releases\4.18.1", true)]               // one downloaded release
    [InlineData(@"Releases\4.0.5455", true)]             // an older feed's long build number
    [InlineData(@"Releases\4.18.1\cli_x64", true)]       // inside a recognised release
    [InlineData(@"Releases\notes", false)]               // not a version, so not a release
    [InlineData(@"Releases\4.18", false)]                // fewer parts than a release carries
    [InlineData(@"Releases\4.18.1-backup", false)]       // something a person made
    public void TheOuterAzureFunctionsRootRecognisesNothingAtAll(string relative, bool allowed)
    {
        var provider = new AzureFunctionsToolsProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(
                relative.Length == 0
                    ? provider.RootPath
                    : Path.Combine(provider.RootPath, relative)).IsAllowed);
    }

    /// <summary>
    /// The same rule over the folder with the most to lose. A Chromium user-data folder keeps the
    /// sign-in cookies, the saved passwords and the saved payment cards directly beside the caches,
    /// and repeats the whole layout inside every profile.
    /// </summary>
    [Theory]
    [InlineData("", false)]                             // the user-data folder itself
    [InlineData("Local State", false)]                  // the key that decrypts the rest
    [InlineData("Login Data", false)]                   // saved passwords
    [InlineData("Default", false)]                      // a whole profile
    [InlineData(@"Default\Cookies", false)]
    [InlineData(@"Default\Web Data", false)]            // saved payment cards
    [InlineData(@"Default\Network", false)]
    [InlineData("GPUCache", true)]                      // a recognised cache
    [InlineData(@"Default\GPUCache", true)]
    [InlineData(@"Default\Cache\Cache_Data", true)]
    [InlineData(@"Default\Cache", false)]               // the container, which stays
    [InlineData(@"Default\Service Worker\CacheStorage", true)]
    public void AChromiumProfileIsClassifiedLevelByLevel(string relative, bool allowed)
    {
        var browser = _temp.CreateDirectory("profile", "AppData", "Local", "TestBrowser");
        _temp.CreateFile(1, "profile", "AppData", "Local", "TestBrowser", "Local State");
        _temp.CreateDirectory("profile", "AppData", "Local", "TestBrowser", "Default");

        var policy = new ExploreActionPolicy([], new ChromiumCacheProvider(_environment).ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? browser : Path.Combine(browser, relative)).IsAllowed);
    }

    /// <summary>
    /// The Epic Games launcher keeps its settings, its cloud saves, its logs and the store's whole
    /// browser profile in one folder, and two providers act inside it. So the declaration has to say
    /// three different things about that one listing: the logs may go, the settings may not, and the
    /// browser folder may not — only the caches named inside it.
    /// </summary>
    [Theory]
    [InlineData("", false)]                                     // the launcher folder itself
    [InlineData("Config", false)]                               // the launcher settings
    [InlineData("Data", false)]                                 // the launcher's own state
    [InlineData("Saves", false)]                                // cloud saves
    [InlineData("UserVaultSettings", false)]
    [InlineData("Crashes", true)]                               // Tier 3, and offered
    [InlineData("Logs", true)]
    [InlineData("webcache_4430", false)]                        // holds the sign-in cookies
    [InlineData(@"webcache_4430\Cookies", false)]
    [InlineData(@"webcache_4430\Local Storage", false)]
    [InlineData(@"webcache_4430\Cache", true)]                  // a recognised cache
    [InlineData(@"webcache_4430\Code Cache", true)]
    [InlineData(@"webcache_4430\Service Worker", false)]        // the container, which stays
    [InlineData(@"webcache_4430\Service Worker\Database", false)]
    [InlineData(@"webcache_4430\Service Worker\CacheStorage", true)]
    [InlineData(@"webcache_4430\Service Worker\ScriptCache", true)]
    public void TheEpicLauncherFolderIsClassifiedLevelByLevel(string relative, bool allowed)
    {
        var saved = _temp.CreateDirectory(
            "profile", "AppData", "Local", "EpicGamesLauncher", "Saved");

        _temp.CreateDirectory(
            "profile", "AppData", "Local", "EpicGamesLauncher", "Saved", "webcache_4430",
            "Service Worker");

        var policy = new ExploreActionPolicy(
            [],
            new EpicLauncherWebCacheProvider(_environment).ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? saved : Path.Combine(saved, relative)).IsAllowed);
    }

    /// <summary>
    /// Two providers act inside the Epic launcher's folder, and both declare it. That is redundant
    /// on a machine where both are registered, and deliberate anyway: a declaration carried by only
    /// one of them would leave Explore willing to remove somebody's launcher settings the moment the
    /// other was the provider dropped.
    ///
    /// <para>The policy reads every provider's roots into one list, so the duplicate has to answer
    /// the same way whichever of the two it resolves to — which is what makes the redundancy free
    /// rather than ambiguous.</para>
    /// </summary>
    [Fact]
    public void TheLauncherFolderAnswersTheSameWhicheverProviderDeclaredIt()
    {
        var saved = _temp.CreateDirectory(
            "profile", "AppData", "Local", "EpicGamesLauncher", "Saved");

        ICleanupProvider[] providers =
        [
            new EpicLauncherWebCacheProvider(_environment),
            new EpicLauncherLogProvider(_environment),
        ];

        var together = new ExploreActionPolicy([], providers.SelectMany(p => p.ToolRoots));

        foreach (var provider in providers)
        {
            var alone = new ExploreActionPolicy([], provider.ToolRoots);

            foreach (var relative in new[] { "Config", "Data", "Saves", "UserVaultSettings", "Crashes", "Logs" })
            {
                var path = Path.Combine(saved, relative);

                Assert.Equal(alone.MayRemove(path).IsAllowed, together.MayRemove(path).IsAllowed);
            }

            Assert.False(together.MayRemove(saved).IsAllowed);
        }
    }

    /// <summary>
    /// A Firefox profile is two directories under two different roots, and only one of them holds
    /// anything Deguffer will remove. The roaming half is declared here precisely because nothing in
    /// the provider ever plans against it: without the declaration a user could delete
    /// <c>logins.json</c> out of the size picture while the Storage page was carefully leaving it
    /// alone.
    /// </summary>
    [Theory]
    [InlineData(true, "", false)]                  // the cache folder itself
    [InlineData(true, "cache2", true)]             // a recognised cache
    [InlineData(true, "startupCache", true)]
    [InlineData(true, "remote-settings", false)]   // recognised, and deliberately not offered
    [InlineData(true, "storage", false)]           // unrecognised, so left alone
    [InlineData(false, "", false)]                 // the profile itself
    [InlineData(false, "logins.json", false)]      // saved passwords
    [InlineData(false, "places.sqlite", false)]    // bookmarks and history
    [InlineData(false, "cache2", false)]           // a cache name in the half that is never touched
    public void AFirefoxProfileIsClassifiedByWhichHalfItIsIn(bool local, string relative, bool allowed)
    {
        RegisterFirefoxProfile();

        var provider = new FirefoxCacheProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots);
        var profile = Assert.Single(provider.Profiles());
        var root = local ? profile.LocalPath : profile.RoamingPath;

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// A Code - OSS editor's user-data folder, level by level. It repeats the Chromium shape one
    /// directory further down: <c>WebStorage</c> holds one directory per webview, and each of those
    /// holds what that view saved beside the one cache Deguffer removes.
    /// </summary>
    [Theory]
    [InlineData("", false)]                             // the user-data folder itself
    [InlineData("User", false)]                         // settings, profiles and extension state
    [InlineData(@"User\workspaceStorage", false)]       // every workspace's restored state
    [InlineData(@"User\History", false)]                // the local undo history
    [InlineData("CachedData", true)]                    // a recognised cache
    [InlineData("CachedExtensionVSIXs", true)]
    [InlineData("Backups", false)]                      // unrecognised, so left alone
    [InlineData("WebStorage", false)]                   // the container, which stays
    [InlineData(@"WebStorage\42", false)]               // one webview's storage, which also stays
    [InlineData(@"WebStorage\42\CacheStorage", true)]
    [InlineData(@"WebStorage\42\Local Storage", false)] // what that webview saved
    public void AVsCodeUserDataFolderIsClassifiedLevelByLevel(string relative, bool allowed)
    {
        var editor = CreateVsCodeFolder();
        _temp.CreateDirectory("profile", "AppData", "Roaming", "Code", "WebStorage", "42");

        var policy = new ExploreActionPolicy([], new VsCodeCacheProvider(_environment).ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? editor : Path.Combine(editor, relative)).IsAllowed);
    }

    /// <summary>
    /// One folder, three owners. A VS Code user-data folder holds Chromium's six engine caches, the
    /// editor's own caches, and the editor's logs, and each set is declared by the provider that
    /// knows it.
    ///
    /// <para>The innermost containing declaration used to be a single root, so whichever provider
    /// happened to be constructed first answered for the whole folder and every child the other two
    /// recognise was refused — silently, and reversibly by reordering a list nobody would think to
    /// look at. Asking every declaration at that depth is what makes each provider's table its own
    /// business.</para>
    /// </summary>
    [Theory]
    [InlineData("Code Cache", true)]            // Chromium's
    [InlineData("GPUCache", true)]              // Chromium's
    [InlineData("CachedData", true)]            // the editor's cache
    [InlineData("CachedProfilesData", true)]    // the editor's cache
    [InlineData("logs", true)]                  // the editor's records
    [InlineData("Crashpad", true)]              // the editor's records
    [InlineData("User", false)]                 // recognised by none of the three
    [InlineData("Local State", false)]
    public void EveryProviderOwningOneFolderAnswersForItsOwnChildren(string child, bool allowed)
    {
        var editor = CreateVsCodeFolder();

        var policy = new ExploreActionPolicy(
            [],
            [
                .. new ChromiumCacheProvider(_environment).ToolRoots,
                .. new VsCodeCacheProvider(_environment).ToolRoots,
                .. new VsCodeLogProvider(_environment).ToolRoots,
            ]);

        Assert.Equal(allowed, policy.MayRemove(Path.Combine(editor, child)).IsAllowed);

        // The folder itself is refused whichever declaration answers for it.
        Assert.False(policy.MayRemove(editor).IsAllowed);
    }

    /// <summary>
    /// A user-data folder carrying both markers: Chromium's, so the engine provider identifies it,
    /// and the editor's global storage database, so the two editor providers do.
    /// </summary>
    private string CreateVsCodeFolder()
    {
        var editor = _temp.CreateDirectory("profile", "AppData", "Roaming", "Code");

        _temp.CreateFile(1, "profile", "AppData", "Roaming", "Code", "Local State");
        _temp.CreateFile(1, "profile", "AppData", "Roaming", "Code", "User", "globalStorage", "state.vscdb");

        return editor;
    }

    /// <summary>
    /// Refusing a profile is worth nothing while the folder holding it can go. Every directory
    /// between the two application-data roots and a profile contains the whole password database,
    /// so each of them is refused as well — otherwise Explore takes the parent of the directory the
    /// Storage page was carefully leaving alone.
    /// </summary>
    [Theory]
    [InlineData(true, "")]              // %APPDATA%\Mozilla\Firefox
    [InlineData(true, "Profiles")]
    [InlineData(true, "profiles.ini")]  // losing it loses every profile
    [InlineData(false, "")]             // %LOCALAPPDATA%\Mozilla\Firefox
    [InlineData(false, "Profiles")]
    public void FirefoxsOwnFoldersAreRefusedAsWellAsTheProfilesInThem(bool roaming, string relative)
    {
        RegisterFirefoxProfile();

        var provider = new FirefoxCacheProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots);

        var root = Path.Combine(
            roaming ? _environment.RoamingAppData : _environment.LocalAppData, "Mozilla", "Firefox");

        Assert.False(
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// Steam's folder in the profile. <c>cefdata</c> and <c>widevine</c> are declared and refused
    /// rather than merely absent from the allow-list, because each has a specific reason it is not
    /// on offer and the generic "not recognised" sentence would be a weaker thing to tell somebody.
    /// </summary>
    [Theory]
    [InlineData("", false)]                     // Steam's own folder
    [InlineData("htmlcache", true)]             // the cache Deguffer removes
    [InlineData(@"htmlcache\Cache", true)]      // and everything under it
    [InlineData("cefdata", false)]              // recognised, and deliberately not offered
    [InlineData("widevine", false)]             // downloaded software rather than a cache
    [InlineData("logs", false)]                 // unrecognised, so left alone
    public void SteamsProfileFolderOffersOnlyTheBrowserCache(string relative, bool allowed)
    {
        var root = Path.Combine(_environment.LocalAppData, "Steam");
        var policy = new ExploreActionPolicy([], new SteamCacheProvider(_environment).ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// The install directory, which is the one tool root in this project that holds the user's game
    /// library. Program Files is refused structurally, but a Steam library is put on a second drive
    /// precisely so that it is not — so the refusal has to come from the declaration.
    ///
    /// <para>Three levels, because the HTTP cache is a level below the install and a
    /// <see cref="ToolRoot"/> classifies immediate children: the install recognises nothing at all,
    /// and <c>appcache</c> under it recognises the one child that may go.</para>
    /// </summary>
    [Theory]
    [InlineData("", false)]                        // where Steam is installed
    [InlineData("steamapps", false)]               // every installed game
    [InlineData(@"steamapps\common\A Game", false)]
    [InlineData(@"steamapps\downloading", false)]  // the half-downloaded part of an update
    [InlineData(@"steamapps\workshop", false)]
    [InlineData("userdata", false)]                // cloud saves and screenshots
    [InlineData("config", false)]                  // who is signed in on this computer
    [InlineData("appcache", false)]                // the container, which stays
    [InlineData(@"appcache\httpcache", true)]      // the one cache offered here
    [InlineData(@"appcache\librarycache", false)]  // recognised, and deliberately not offered
    [InlineData("something-unrecognised", false)]
    public void SteamsInstallDirectoryOffersOnlyTheHttpCache(string relative, bool allowed)
    {
        var install = RegisterSteamInstall();
        var policy = new ExploreActionPolicy([], new SteamCacheProvider(_environment).ToolRoots);

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? install : Path.Combine(install, relative)).IsAllowed);
    }

    /// <summary>
    /// Every provider that declares a root refuses an unrecognised sibling inside it, and refuses
    /// the root itself.
    ///
    /// <para>G8 asks for the unrecognised case on every tier classification, because that is the
    /// direction that loses data. The names below are each taken from what the provider's own plan
    /// asserts must survive, so this is the §5.6 promise read back through the second deletion
    /// route.</para>
    /// </summary>
    [Theory]
    [InlineData("gradle", "gradle.properties")]              // signing keys and credentials
    [InlineData("cargo", "credentials.toml")]                // registry tokens
    [InlineData("nuget", "NuGet.Config")]                    // private feed credentials
    [InlineData("maven", "settings-security.xml")]           // the master password
    [InlineData("platformio", "packages")]                   // the installed toolchains
    [InlineData("uv", "tools")]                              // what 'uv tool install' put there
    [InlineData("pip", "pip.ini")]                           // private index URLs
    [InlineData("go", "src")]                                // the user's own code
    [InlineData("vscode-cpptools", "something-unrecognised")]
    [InlineData("dart-analysis-server", ".prompts")]         // the user's answers to the server's prompts
    [InlineData("playwright", ".links")]                     // how Playwright resolves a build
    [InlineData("gpu-shader-cache", "accounts")]             // NVIDIA's, and not a cache
    [InlineData("epic-launcher-webcache", "Config")]         // the launcher settings
    [InlineData("epic-launcher-logs", "UserVaultSettings")]
    [InlineData("steam", "cefdata")]                         // the embedded browser's working data
    public void EveryDeclaredRootRefusesAnUnrecognisedSibling(string providerId, string sibling)
    {
        var provider = Providers().Single(p => p.Id == providerId);
        var policy = new ExploreActionPolicy([], provider.ToolRoots);

        Assert.NotEmpty(provider.ToolRoots);

        foreach (var root in provider.ToolRoots)
        {
            Assert.False(policy.MayRemove(root.Path).IsAllowed);
        }

        Assert.False(policy.MayRemove(Path.Combine(provider.ToolRoots[0].Path, sibling)).IsAllowed);
    }

    /// <summary>
    /// An install Steam's own record points at, carrying the client itself — which is what
    /// <see cref="SteamDiscovery"/> requires before it treats a recorded path as an install.
    ///
    /// The recorded value is written in Steam's own form, with forward slashes, because that is
    /// what the client writes.
    /// </summary>
    private string RegisterSteamInstall()
    {
        var root = _temp.CreateDirectory("games", "Steam");
        _temp.CreateFile(64, "games", "Steam", "steam.exe");

        _environment.WithRegistryValue(
            SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, root.Replace('\\', '/'));

        return root;
    }

    /// <summary>
    /// A profile's own two directories come from Mozilla's register rather than from a known path,
    /// so the provider declares no <em>profile</em> root until <c>profiles.ini</c> names one.
    /// Firefox's two folders above them are declared from constants and need no fixture.
    /// </summary>
    private void RegisterFirefoxProfile() => File.WriteAllText(
        _temp.CreateFile(0, "profile", "AppData", "Roaming", "Mozilla", "Firefox", "profiles.ini"),
        """
        [Profile0]
        Name=default-release
        IsRelative=1
        Path=Profiles/default-release
        """);

    /// <summary>
    /// Firefox and the Azure Functions tooling are deliberately absent. The sweep above probes a
    /// sibling of <c>ToolRoots[0]</c>, and each of those providers declares a first root that
    /// recognises nothing at all, so the probe would be refused structurally rather than by the
    /// allow-list — an assertion that cannot fail. Each has its own theory instead, covering every
    /// root it declares and the unrecognised sibling properly.
    /// </summary>
    private IReadOnlyList<ICleanupProvider> Providers() =>
    [
        new GradleCacheProvider(_environment),
        new CargoCacheProvider(_environment),
        new NuGetCacheProvider(_environment),
        new MavenRepositoryProvider(_environment),
        new PlatformIoCacheProvider(_environment),
        new UvCacheProvider(_environment),
        new PipCacheProvider(_environment),
        new GoCacheProvider(_environment),
        new VsCodeCppToolsCacheProvider(_environment),
        new DartAnalysisServerProvider(_environment),
        new PlaywrightBrowsersProvider(_environment),
        new GpuShaderCacheProvider(_environment),
        new EpicLauncherWebCacheProvider(_environment),
        new EpicLauncherLogProvider(_environment),

        // Its install directory is deliberately not registered here. The sweep probes ToolRoots[0],
        // which is Steam's folder in the profile and is declared from a constant; the install root
        // and the container under it have their own theory above, where the register is set up.
        new SteamCacheProvider(_environment),
    ];

    /// <summary>
    /// The wiring, once, through a real provider: <see cref="ExploreActionPolicy.For"/> reads §5.2
    /// out of the providers rather than restating it, so a provider's own declaration is what
    /// Explore enforces.
    /// </summary>
    [Fact]
    public void ThePolicyReadsSection52OutOfTheProvidersThemselves()
    {
        var provider = new GradleCacheProvider(_environment);
        var policy = ExploreActionPolicy.For(_system, _environment, [provider]);

        Assert.Equal(GradleRoot, provider.RootPath);
        Assert.False(policy.MayRemove(provider.RootPath).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(provider.RootPath, "gradle.properties")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(provider.RootPath, "caches")).IsAllowed);
    }

    private string GradleRoot => Path.Combine(_environment.UserProfile, ".gradle");

    private ToolRoot Gradle() =>
        ToolRoot.Of(GradleRoot, "Gradle's own folder.", GradleCacheProvider.DisposableChildren);

    private ExploreActionPolicy Policy(params ToolRoot[] toolRoots) =>
        ExploreActionPolicy.For(_system, _environment, [new StubProvider(toolRoots)]);

    private sealed class StubProvider(IReadOnlyList<ToolRoot> roots) : ICleanupProvider
    {
        public string Id => "stub";

        public string Name => "Stub";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderDescription Description { get; } = new()
        {
            Application = "A stub, standing in for a real toolchain.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => roots;

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public void InvalidateCaches()
        {
        }

        public Task<Execution.CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.CleanupResult> ExecuteAsync(
            Execution.CleanupPlan plan,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.VerificationResult> VerifyAsync(
            Execution.CleanupPlan plan, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");
    }
}
