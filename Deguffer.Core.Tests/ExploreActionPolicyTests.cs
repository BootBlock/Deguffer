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
    private readonly FakeVolumeInventory _volumes = new();

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
    /// §9's Outlook data files, by their type rather than by where they are. A <c>.pst</c> is
    /// wherever somebody saved it, so every case here is somewhere no region and no tool root covers:
    /// a rule that only knew Outlook's own folders would allow all of them.
    /// </summary>
    [Theory]
    [InlineData(@"Documents\archive.pst")]
    [InlineData(@"Downloads\old mailbox.OST")]
    [InlineData(@"OneDrive\Mail\2019.Pst")]
    public void AnOutlookDataFileIsRefusedWhereverItIsSaved(string relative)
    {
        var verdict = Policy().MayRemove(Path.Combine(_environment.UserProfile, relative));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("Outlook", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// And on a drive the policy was never told about, at the top of it or anywhere below — the
    /// archive on a data disk is the ordinary case for a file that is kept because it is the only
    /// copy.
    /// </summary>
    [Theory]
    [InlineData(@"D:\archive.pst")]
    [InlineData(@"D:\Mail\2014\archive.pst")]
    [InlineData(@"E:\Backups\mailbox.ost")]
    public void AnOutlookDataFileIsRefusedOnAnyDrive(string path)
    {
        Assert.False(Policy().MayRemove(path).IsAllowed);
    }

    /// <summary>
    /// §5.2's shape, applied to Outlook's own folder: nothing in it is recognised, so the folder and
    /// everything in it are refused — the address books and caches beside the mailbox as much as
    /// the mailbox, and a name nobody here has heard of most of all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("RoamCache")]
    [InlineData(@"Offline Address Books\udetails.oab")]
    [InlineData("something-unrecognised.dat")]
    public void OutlooksOwnFolderIsRefusedWithEverythingInIt(string relative)
    {
        var verdict = Policy().MayRemove(
            Path.Combine(_environment.LocalAppData, "Microsoft", "Outlook", relative));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("Outlook", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder Outlook saves new data files in, wherever the Documents folder holding it has
    /// been moved to. Removing the folder is removing every file in it, so refusing only the files
    /// would leave the same deletion one level up.
    /// </summary>
    [Theory]
    [InlineData(@"Documents\Outlook Files")]
    [InlineData(@"OneDrive\Documents\Outlook Files")]
    [InlineData(@"OneDrive\Documents\Outlook Files\readme.txt")]
    public void TheFolderOutlookSavesDataFilesInIsRefusedWhereverItIs(string relative)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_environment.UserProfile, relative)).IsAllowed);
    }

    /// <summary>
    /// The over-reach direction. Each of these only resembles an Outlook data file or one of its
    /// folders, and a refusal matched on text rather than on the name would take away a removal
    /// the user is entitled to without protecting any mail.
    /// </summary>
    [Theory]
    [InlineData(@"Documents\archive.pst.txt")]
    [InlineData(@"Documents\archive.pstx")]
    [InlineData(@"Documents\pst")]
    [InlineData(@"Documents\Outlook Files backup")]
    [InlineData(@"AppData\Local\Microsoft\OutlookBackup")]
    [InlineData(@"AppData\Local\Microsoft\Edge")]
    public void ANameThatOnlyResemblesOutlooksIsNotRefusedForIt(string relative)
    {
        Assert.True(Policy().MayRemove(Path.Combine(_environment.UserProfile, relative)).IsAllowed);
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
    /// <see cref="Deguffer.Core.Safety.IVolumeInventory"/>'s list of volumes, which is a snapshot — so
    /// a volume mounted after the page opened was scannable with its paging file and its restore
    /// points unprotected. Where the machine says nothing about a volume the path's own root answers,
    /// and this asserts it against a letter no fake ever mentioned.</para>
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
    /// Everything inside a volume's Recycle Bin, where each account keeps a folder of what it deleted.
    /// The Recycle Bin provider names every other account's folder as a path that must survive, so
    /// refusing the bin alone left them one level down.
    /// </summary>
    [Theory]
    [InlineData(@"$Recycle.Bin\S-1-5-21-1000-1000-1000-1001")]
    [InlineData(@"$Recycle.Bin\S-1-5-21-1000-1000-1000-1002\$RQ4ZKJX.txt")]
    [InlineData(@"$RECYCLE.BIN\S-1-5-21-1000-1000-1000-1002\$IQ4ZKJX.txt")]
    public void EverythingInsideARecycleBinIsRefused(string relative)
    {
        var verdict = Policy().MayRemove(Path.Combine(@"Q:\", relative));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("Recycle Bin", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The over-reach direction: the rule is about the bin at the top of a volume, not about the name.
    /// </summary>
    [Theory]
    [InlineData(@"Stuff\$Recycle.Bin\S-1-5-21-1000-1000-1000-1001")]
    [InlineData(@"$Recycle.Bin.old\notes.txt")]
    public void ARecycleBinNameElsewhereIsOrdinary(string relative)
    {
        Assert.True(Policy().MayRemove(Path.Combine(@"Q:\", relative)).IsAllowed);
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
    /// What Windows and NTFS keep at the top of a volume, on a volume mounted at a folder. Its top
    /// is that folder, so <c>Q:\Mount\pagefile.sys</c> is the volume's paging file. Read from the
    /// drive letter it was one level below <c>Q:\</c> under a folder called <c>Mount</c>, and every
    /// one of these was offered for deletion — another account's deleted files among them.
    /// </summary>
    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("$MFT")]
    [InlineData(@"$Extend\$UsnJrnl")]
    [InlineData("$Recycle.Bin")]
    [InlineData(@"$Recycle.Bin\S-1-5-21-1000-1000-1000-1002\$RQ4ZKJX.txt")]
    public void WhatWindowsKeepsAtTheTopOfAVolumeMountedAtAFolderIsRefused(string relative)
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\Mount", relative)).IsAllowed);
    }

    /// <summary>
    /// The §5.6 half. The rest of a mounted volume stays ordinary, and so do the same names in a
    /// folder nothing is mounted at, or one whose name only starts like the mount point's. What
    /// decides is where the volume is mounted, not what the folder is called.
    /// </summary>
    [Theory]
    [InlineData(@"Q:\Mount\Holiday photos")]
    [InlineData(@"Q:\Mount\Holiday photos\pagefile.sys")]
    [InlineData(@"Q:\Plain\pagefile.sys")]
    [InlineData(@"Q:\Plain\$Recycle.Bin\S-1-5-21-1000-1000-1000-1002")]
    [InlineData(@"Q:\Mountains\$MFT")]
    public void EverythingElseOnOrBesideAVolumeMountedAtAFolderIsOrdinary(string path)
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        Assert.True(Policy().MayRemove(path).IsAllowed);
    }

    /// <summary>
    /// The folder a volume is mounted at is that whole volume. Read from the drive letter it was an
    /// ordinary folder of <c>Q:</c>.
    /// </summary>
    [Fact]
    public void TheFolderAVolumeIsMountedAtIsRefusedAsAWholeDrive()
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        var verdict = Policy().MayRemove(@"Q:\Mount");

        Assert.False(verdict.IsAllowed);
        Assert.Contains("whole drive", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A volume mounted after the policy was built is covered. The policy lives for a scan and the
    /// folder picker can reach a volume mounted a moment ago, so it asks at each question.
    /// </summary>
    [Fact]
    public void AVolumeMountedAfterThePolicyWasBuiltIsCovered()
    {
        _volumes.With(@"Q:\");
        var policy = Policy();

        Assert.True(policy.MayRemove(@"Q:\Mount\pagefile.sys").IsAllowed);

        _volumes.With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        Assert.False(policy.MayRemove(@"Q:\Mount\pagefile.sys").IsAllowed);
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
            [], new FakeVolumeInventory());

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
    /// Removing a folder removes everything in it, so a folder holding a tool's root is refused as the
    /// root is, and says which root. What else it holds stays ordinary: the rule is about what a folder
    /// holds, not about sitting near a tool.
    /// </summary>
    [Fact]
    public void AFolderHoldingAToolRootIsRefusedAndSaysWhichRoot()
    {
        var vendor = Path.Combine(_environment.LocalAppData, "Vendor");
        var root = _temp.CreateDirectory("profile", "AppData", "Local", "Vendor", "Tool");
        var policy = Policy(VendorTool(root));

        var verdict = policy.MayRemove(vendor);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(root, verdict.Reason, StringComparison.Ordinal);
        Assert.True(policy.MayRemove(Path.Combine(vendor, "Other")).IsAllowed);
    }

    /// <summary>
    /// The same folder with no root in it, which is what a tool leaves behind once it is uninstalled.
    /// Nothing protected would go with it, so a refusal would name a folder that is not there and take
    /// away a removal the user is entitled to. The root is then created, so the refusal is shown to
    /// follow the disk rather than the declaration.
    /// </summary>
    [Fact]
    public void AFolderIsNotRefusedForARootThatIsNotOnDisk()
    {
        var vendor = _temp.CreateDirectory("profile", "AppData", "Local", "Vendor");
        _temp.CreateFile(64, "profile", "AppData", "Local", "Vendor", "leftover.log");
        var policy = Policy(VendorTool(Path.Combine(vendor, "Tool")));

        Assert.True(policy.MayRemove(vendor).IsAllowed);

        Directory.CreateDirectory(Path.Combine(vendor, "Tool"));

        Assert.False(policy.MayRemove(vendor).IsAllowed);
    }

    /// <summary>A root can be a file, and a folder holding one takes it along the same way.</summary>
    [Fact]
    public void AFolderHoldingAFileDeclaredAsARootIsRefused()
    {
        var file = _temp.CreateFile(8, "profile", "AppData", "Roaming", "Vendor", "vendor.config");

        Assert.False(Policy(VendorTool(file)).MayRemove(Path.GetDirectoryName(file)!).IsAllowed);
    }

    /// <summary>
    /// A root inside a child its parent root recognises. The child is on offer by §5.2, and removing it
    /// would still take the deeper root along, so what the child holds decides as well.
    /// </summary>
    [Fact]
    public void ARecognisedChildHoldingADeeperRootIsRefused()
    {
        var deeper = _temp.CreateDirectory("profile", ".gradle", "caches", "kept");
        var policy = Policy(Gradle(), VendorTool(deeper));

        Assert.False(policy.MayRemove(Path.Combine(GradleRoot, "caches")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(GradleRoot, "wrapper")).IsAllowed);
    }

    /// <summary>
    /// A folder holding something the region table refuses: Outlook's own folder, whose offline mailbox
    /// and address books go with <c>Microsoft</c> as surely as with the folder itself. The premise comes
    /// first, that the same folder without Outlook's in it is ordinary.
    /// </summary>
    [Fact]
    public void AFolderHoldingARefusedRegionIsRefused()
    {
        var microsoft = _temp.CreateDirectory("profile", "AppData", "Local", "Microsoft");

        Assert.True(Policy().MayRemove(microsoft).IsAllowed);

        Directory.CreateDirectory(Path.Combine(microsoft, "Outlook"));
        var verdict = Policy().MayRemove(microsoft);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("Outlook", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.1 refuses every path a provider names as protected, and providers name %LOCALAPPDATA% and
    /// %TEMP% as paths that must survive a clean. Each is refused as a folder and ordinary inside, in
    /// the profile's own shape, and so are the other two application-data folders beside them.
    /// </summary>
    [Theory]
    [InlineData(nameof(IUserEnvironment.LocalAppData))]
    [InlineData(nameof(IUserEnvironment.RoamingAppData))]
    [InlineData(nameof(IUserEnvironment.LocalLowAppData))]
    [InlineData(nameof(IUserEnvironment.TempPath))]
    public void TheApplicationDataFoldersAndTheTemporaryFolderAreRefusedButNotWhatIsInThem(string which)
    {
        // The temporary folder moves inside the profile, where Windows puts it, for its own row alone.
        // The fake's default sits beside the profile, which the table already refuses as another
        // account's; and moved for every row it would sit inside %LOCALAPPDATA% and refuse that row
        // by what it holds, whatever that row's own entry said.
        var folder = which switch
        {
            nameof(IUserEnvironment.LocalAppData) => _environment.LocalAppData,
            nameof(IUserEnvironment.RoamingAppData) => _environment.RoamingAppData,
            nameof(IUserEnvironment.LocalLowAppData) => _environment.LocalLowAppData!,
            _ => _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp")).TempPath,
        };

        var policy = Policy();
        var verdict = policy.MayRemove(folder);

        Assert.False(verdict.IsAllowed);
        Assert.DoesNotContain("holds", verdict.Reason, StringComparison.Ordinal);
        Assert.True(policy.MayRemove(Path.Combine(folder, "Some Program")).IsAllowed);
    }

    /// <summary>
    /// The folder above the application-data folders, which no table names. It holds them, so it is
    /// refused for what it holds.
    /// </summary>
    [Fact]
    public void TheAppDataFolderIsRefusedForTheFoldersInIt()
    {
        var verdict = Policy().MayRemove(Path.GetDirectoryName(_environment.LocalAppData)!);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(_environment.LocalAppData, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.3 for the one question the policy asks the disk. Its answer is a boolean, so a deep tree
    /// would prove nothing; the form of the path handed to the filesystem is what discriminates.
    /// </summary>
    [Fact]
    public void AsksTheDiskAboutAHeldLocationInExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("profile", "AppData", "Local", "Vendor", "Tool");
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var policy = new ExploreActionPolicy([], [VendorTool(root)], new FakeVolumeInventory(), recording);

        Assert.False(policy.MayRemove(Path.GetDirectoryName(root)!).IsAllowed);
        Assert.NotEmpty(recording.Paths);
        Assert.All(recording.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// The probe fails closed. Only Windows saying nothing is there reads as absent: a name the
    /// filesystem will not even look up is not an answer, and it reads as present, which refuses the
    /// folder holding it rather than removing it.
    /// </summary>
    [Fact]
    public void AnEntryThatCannotBeAskedAboutMayExist()
    {
        var folder = _temp.CreateDirectory("probe");
        var file = _temp.CreateFile(8, "probe", "present.txt");

        Assert.True(WindowsFileSystem.Default.MayExist(LongPath.Extended(folder)));
        Assert.True(WindowsFileSystem.Default.MayExist(LongPath.Extended(file)));
        Assert.False(WindowsFileSystem.Default.MayExist(LongPath.Extended(Path.Combine(folder, "absent"))));
        Assert.False(WindowsFileSystem.Default.MayExist(LongPath.Extended(Path.Combine(folder, "absent", "deeper"))));
        Assert.True(WindowsFileSystem.Default.MayExist(LongPath.Extended(folder) + @"\not<a>name"));
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
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());
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
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(
                relative.Length == 0
                    ? provider.RootPath
                    : Path.Combine(provider.RootPath, relative)).IsAllowed);
    }

    /// <summary>
    /// The same rule over Affinity, where what sits beside the cache is the user's whole asset
    /// library and the records that keep the product activated. Three levels declare it: the profile
    /// root and the shared folder recognise nothing at all, and only a version folder lets one child
    /// go.
    /// </summary>
    [Theory]
    [InlineData("", false)]                                         // Affinity's own folder
    [InlineData("Photo", false)]                                    // a product tree
    [InlineData(@"Photo\2.0\autosave", false)]                      // unsaved-document recovery
    [InlineData("Common", false)]                                   // the shared folder, never a target
    [InlineData(@"Common\2.0", false)]                              // the asset library and the licences
    [InlineData(@"Common\2.0\user", false)]                         // the asset library itself
    [InlineData(@"Common\2.0\Licences", false)]                     // what keeps the product activated
    [InlineData(@"Common\2.0\modelcache", true)]                    // the downloaded models
    [InlineData(@"Common\2.0\modelcache\Saliency_2.6.onnx", true)]  // inside them
    [InlineData(@"Common\3", false)]                                // not how Affinity names a version
    [InlineData(@"Common\3\modelcache", false)]                     // so nothing in it is reached either
    public void AnAffinityProfileIsClassifiedLevelByLevel(string relative, bool allowed)
    {
        const string Affinity = AffinityProfiles.ProfileFolderName;

        var root = _temp.CreateDirectory("profile", Affinity);
        _temp.CreateDirectory("profile", Affinity, "Photo", "2.0", "autosave");
        _temp.CreateDirectory("profile", Affinity, "Common", "2.0", "user");
        _temp.CreateDirectory("profile", Affinity, "Common", "2.0", "Licences");
        _temp.CreateDirectory("profile", Affinity, "Common", "2.0", "modelcache");
        _temp.CreateDirectory("profile", Affinity, "Common", "3", "modelcache");

        var policy = new ExploreActionPolicy([], new AffinityModelCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
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

        var policy = new ExploreActionPolicy([], new ChromiumCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

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
            new EpicLauncherWebCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

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

        var together = new ExploreActionPolicy([], providers.SelectMany(p => p.ToolRoots), new FakeVolumeInventory());

        foreach (var provider in providers)
        {
            var alone = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

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
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());
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

        var policy = new ExploreActionPolicy([], new VsCodeCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

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
            ], new FakeVolumeInventory());

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
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

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
        var policy = new ExploreActionPolicy([], new SteamCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

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
        var policy = new ExploreActionPolicy([], new SteamCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? install : Path.Combine(install, relative)).IsAllowed);
    }

    /// <summary>
    /// Spotify's folder in the profile, where the music and podcasts the user downloaded sit beside
    /// the streaming cache.
    /// </summary>
    [Theory]
    [InlineData("", false)]                // Spotify's own folder
    [InlineData("Data", true)]             // the streaming cache
    [InlineData(@"Data\0a", true)]         // and everything under it
    [InlineData("Storage", false)]         // the downloads, which need Premium to get back
    [InlineData(@"Storage\0a", false)]
    [InlineData("offline.bnk", false)]     // the record of what was downloaded
    [InlineData("Browser", false)]         // unrecognised, so left alone
    public void SpotifysFolderOffersOnlyTheStreamingCache(string relative, bool allowed)
    {
        var root = Path.Combine(_environment.LocalAppData, "Spotify");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// The installer edition's settings folder, and the Store edition's package folder. A
    /// <see cref="ToolRoot"/> classifies immediate children, so the Store edition's cache is allowed
    /// only because the folder holding it is declared as well, and everything else in the package is
    /// refused.
    /// </summary>
    [Theory]
    [InlineData(false, "", false)]                                // the installer edition's settings
    [InlineData(false, "Users", false)]
    [InlineData(false, "prefs", false)]
    [InlineData(true, "", false)]                                 // the Store edition's package
    [InlineData(true, "LocalState", false)]
    [InlineData(true, @"LocalCache\Spotify", false)]
    [InlineData(true, @"LocalCache\Spotify\Data", true)]          // the Store edition's cache
    [InlineData(true, @"LocalCache\Spotify\Browser", false)]
    [InlineData(true, @"LocalState\Spotify\Storage", false)]      // its downloads
    [InlineData(true, @"LocalState\Spotify\Users", false)]
    public void SpotifysOtherFoldersOfferOnlyTheStoreEditionsCache(bool store, string relative, bool allowed)
    {
        var root = store
            ? Path.Combine(_environment.LocalAppData, "Packages", SpotifyEdition.StorePackageFamily)
            : Path.Combine(_environment.RoamingAppData, "Spotify");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// Storage Spotify's settings moved somewhere else is refused whole, because it may be the
    /// downloads. A drive is the exception, and it is asserted as one: refusing every child of a drive
    /// would take the drive away from Explore for one setting.
    /// </summary>
    [Fact]
    public void AMovedSpotifyStorageIsRefusedExceptWhereItIsAWholeDrive()
    {
        var moved = _temp.CreateDirectory("music", "Spotify");
        WriteSpotifySettings(
            $"storage.location=\"{moved.Replace(@"\", @"\\")}\"",
            @"storage.last-location=""Q:\\""");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(moved).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(moved, "0a")).IsAllowed);

        Assert.True(policy.MayRemove(@"Q:\Holiday photos").IsAllowed);
    }

    /// <summary>
    /// The drive exception does not reach a folder a volume is mounted at. The storage there is
    /// still refused whole, because leaving it out would make the downloads it may hold removable,
    /// and a refusal is the direction §5.2 takes. The policy recognises the folder as a volume
    /// root, so the premise is asserted too: without it the refusal below would prove nothing.
    /// </summary>
    [Fact]
    public void AMovedSpotifyStorageAtAFolderAVolumeIsMountedAtIsStillRefused()
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);
        WriteSpotifySettings(@"storage.location=""Q:\\Mount""");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, _volumes);

        Assert.Contains("whole drive", policy.MayRemove(@"Q:\Mount").Reason, StringComparison.Ordinal);

        Assert.False(policy.MayRemove(@"Q:\Mount\Storage").IsAllowed);
        Assert.False(policy.MayRemove(@"Q:\Mount\Holiday photos").IsAllowed);
        Assert.True(policy.MayRemove(@"Q:\Plain\Holiday photos").IsAllowed);
    }

    /// <summary>
    /// A volume mounted at a folder inside a drive's Recycle Bin. Where it is mounted makes what is
    /// under it look like the top of an ordinary volume, and the drive letter still reads it as inside
    /// the bin. A refusal on either reading holds, so asking the machine never takes one away.
    /// </summary>
    [Fact]
    public void AVolumeMountedInsideARecycleBinDoesNotOpenIt()
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\$Recycle.Bin\S-1-5-21-1000\Archive\"]);

        Assert.False(Policy().MayRemove(@"Q:\$Recycle.Bin\S-1-5-21-1000\Archive\notes.txt").IsAllowed);
    }

    /// <summary>
    /// A cache the Storage page withholds is refused here too, or Explore would offer the one
    /// directory the plan has just declined, for the reason it declined it. Withheld because the
    /// storage was moved inside it, and withheld because the settings name a location nobody can
    /// place.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASpotifyCacheTheStoragePageWithholdsIsRefused(bool movedInside)
    {
        var cache = Path.Combine(_environment.LocalAppData, "Spotify", "Data");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        // The premise, without which the refusal below proves nothing: with no settings file, the
        // cache is allowed.
        Assert.True(policy.MayRemove(cache).IsAllowed);

        WriteSpotifySettings(movedInside
            ? $"storage.location=\"{Path.Combine(cache, "offline").Replace(@"\", @"\\")}\""
            : "storage.location=\"relative\"");
        policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(cache).IsAllowed);
    }

    /// <summary>
    /// Storage moved to a folder above Spotify's own. It holds the cache folder, so the Storage page
    /// withholds the cache, and the folder itself is refused here as well: what the storage put in
    /// it is not distinguishable from anything else there.
    /// </summary>
    [Fact]
    public void AMovedSpotifyStorageAboveSpotifysOwnFolderIsRefused()
    {
        WriteSpotifySettings($"storage.location=\"{_environment.LocalAppData.Replace(@"\", @"\\")}\"");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(_environment.LocalAppData).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_environment.LocalAppData, "0a")).IsAllowed);
    }

    /// <summary>
    /// A location that can be placed, beside one that cannot, is still refused. The file as a whole
    /// cannot say where the storage is, and that does not make the location it does name any less
    /// likely to hold downloads.
    /// </summary>
    [Fact]
    public void AMovedSpotifyStorageBesideAnUnplaceableOneIsStillRefused()
    {
        var moved = _temp.CreateDirectory("music", "Spotify");
        WriteSpotifySettings(
            "storage.location=\"relative\"",
            $"storage.last-location=\"{moved.Replace(@"\", @"\\")}\"");
        var policy = new ExploreActionPolicy([], new SpotifyCacheProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(moved).IsAllowed);
    }

    private void WriteSpotifySettings(params string[] lines) => File.WriteAllText(
        _temp.CreateFile(0, "profile", "AppData", "Roaming", "Spotify", "prefs"),
        string.Join('\n', lines) + "\n");

    /// <summary>
    /// A Squirrel application's own folder, which nothing else in §7.1 refuses: it sits under
    /// <c>%LOCALAPPDATA%</c> like any other application's data, and until a provider says whose it
    /// is, Explore treats it as an ordinary directory somebody may delete.
    ///
    /// <para>Two providers own this one path with disjoint tables, so both are handed to the policy
    /// together — the staging provider declares the packages folder inside it, and the superseded
    /// provider declares the root itself. A child either recognises is allowed, and everything else
    /// is refused.</para>
    /// </summary>
    [Theory]
    [InlineData("", false)]                     // where the application is installed
    [InlineData("Update.exe", false)]           // the updater, and what its shortcut runs
    [InlineData("app-3.6.4", false)]            // the build in use
    [InlineData(@"app-3.6.4\resources", false)]
    [InlineData("app-3.6.3", true)]             // the build it replaced, which is the one on offer
    [InlineData(@"app-3.6.3\resources", true)]
    [InlineData("packages", false)]             // the index a shortcut reads to pick a build
    [InlineData(@"packages\RELEASES", false)]
    [InlineData("app.ico", false)]
    [InlineData("something-unrecognised", false)]
    public void ASquirrelApplicationOffersOnlyABuildItHasReplaced(string relative, bool allowed)
    {
        var root = RegisterSquirrelApplication();

        var policy = new ExploreActionPolicy(
            [],
            [
                .. new SquirrelStagingProvider(_environment).ToolRoots,
                .. new SquirrelSupersededVersionProvider(_environment).ToolRoots,
            ], new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// Squirrel's staging folder, which every application using the updater shares. Only the
    /// directories its own name generator produced are on offer; the setup logs beside them are not.
    /// </summary>
    [Theory]
    [InlineData("", false)]             // the shared staging folder itself
    [InlineData("tempa", true)]         // an install or update it unpacked
    [InlineData(@"tempa\lib", true)]
    [InlineData("temp", false)]         // the prefix alone is not one of its names
    [InlineData("SquirrelSetup.log", false)]
    [InlineData("setup.json", false)]
    public void SquirrelsStagingFolderOffersOnlyWhatItUnpacked(string relative, bool allowed)
    {
        var root = _temp.CreateDirectory("profile", "AppData", "Local", "SquirrelTemp");

        var policy = new ExploreActionPolicy(
            [], new SquirrelStagingProvider(_environment).ToolRoots, new FakeVolumeInventory());

        Assert.Equal(
            allowed,
            policy.MayRemove(relative.Length == 0 ? root : Path.Combine(root, relative)).IsAllowed);
    }

    /// <summary>
    /// An application installed by Squirrel: the updater it puts beside every one it manages, and a
    /// directory per build. The provider treats neither half alone as an installation, so both are
    /// needed before it declares anything at all.
    /// </summary>
    private string RegisterSquirrelApplication()
    {
        var root = _temp.CreateDirectory("profile", "AppData", "Local", "Chatterbox");

        _temp.CreateFile(64, "profile", "AppData", "Local", "Chatterbox", "Update.exe");
        _temp.CreateDirectory("profile", "AppData", "Local", "Chatterbox", "app-3.6.3");
        _temp.CreateDirectory("profile", "AppData", "Local", "Chatterbox", "app-3.6.4");
        _temp.CreateDirectory("profile", "AppData", "Local", "Chatterbox", "packages");

        return root;
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
    [InlineData("poetry", "virtualenvs")]                    // every environment on the machine
    [InlineData("go", "src")]                                // the user's own code
    [InlineData("vscode-cpptools", "something-unrecognised")]
    [InlineData("dart-analysis-server", ".prompts")]         // the user's answers to the server's prompts
    [InlineData("roslyn-cache", "something-unrecognised")]
    [InlineData("playwright", ".links")]                     // how Playwright resolves a build
    [InlineData("gpu-shader-cache", "accounts")]             // NVIDIA's, and not a cache
    [InlineData("epic-launcher-webcache", "Config")]         // the launcher settings
    [InlineData("epic-launcher-logs", "UserVaultSettings")]
    [InlineData("steam", "cefdata")]                         // the embedded browser's working data
    [InlineData("spotify", "Storage")]                       // the downloads, which need Premium to get back
    public void EveryDeclaredRootRefusesAnUnrecognisedSibling(string providerId, string sibling)
    {
        var provider = Providers().Single(p => p.Id == providerId);
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.NotEmpty(provider.ToolRoots);

        foreach (var root in provider.ToolRoots)
        {
            Assert.False(policy.MayRemove(root.Path).IsAllowed);
        }

        Assert.False(policy.MayRemove(Path.Combine(provider.ToolRoots[0].Path, sibling)).IsAllowed);
    }

    /// <summary>
    /// §5.2 over Roslyn's indexes, where the inner level recognises a program's set by what it holds
    /// rather than by its name. A directory shaped like a set that holds something else is refused, as the
    /// plan declines it, so Explore cannot remove what the Storage page calls Tier 4 — and the premise is
    /// asserted first: the impostor's name alone passes, so only its contents can refuse it.
    ///
    /// <para>Visual Studio's own folder is refused as well, with its unsaved-document recovery and anything
    /// else in it, because the plan names them protected and §7.1 refuses every such path. Refusing
    /// <c>Roslyn</c> is worth nothing while the folder holding it can go.</para>
    /// </summary>
    [Fact]
    public void RoslynsCacheIsRecognisedByWhatASetHoldsAndNothingAboveASetIsRemovable()
    {
        var provider = new RoslynCacheProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());
        var cache = provider.CachePath;
        var roslyn = Path.GetDirectoryName(cache)!;

        var recognised = RoslynCacheFixture.CreateHost(cache, RoslynCacheFixture.Host, RoslynCacheFixture.Solution);
        var impostor = RoslynCacheFixture.CreateHost(cache, RoslynCacheFixture.OtherHost, RoslynCacheFixture.Solution);

        Assert.True(policy.MayRemove(impostor).IsAllowed);
        File.WriteAllBytes(Path.Combine(impostor, "notes.txt"), new byte[8]);

        Assert.True(policy.MayRemove(recognised).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(recognised, RoslynCacheFixture.Solution, "sqlite3")).IsAllowed);

        Assert.False(policy.MayRemove(impostor).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(impostor, "notes.txt")).IsAllowed);
        Assert.False(policy.MayRemove(cache).IsAllowed);
        Assert.False(policy.MayRemove(roslyn).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(roslyn, "something-unrecognised")).IsAllowed);

        var visualStudio = Path.GetDirectoryName(roslyn)!;
        Assert.False(policy.MayRemove(visualStudio).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(visualStudio, "BackupFiles")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(visualStudio, "BackupFiles", "Unsaved.cs")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(visualStudio, "17.0_testinstance")).IsAllowed);
    }

    /// <summary>
    /// A name that is disposable under one of a provider's roots must still be refused under
    /// another, and the shader caches are where that now bites: NVIDIA keeps a root in each of two
    /// application-data tiers, and <c>GLCache</c> is declared in only one of them.
    ///
    /// <para><see cref="EveryDeclaredRootRefusesAnUnrecognisedSibling"/> cannot cover this. It
    /// probes one root per provider with a name unrecognised everywhere, and the case here is the
    /// opposite one: a name the policy would allow if it matched on the child rather than on the
    /// root it sits under.</para>
    /// </summary>
    [Fact]
    public void ANameDisposableInOneTierIsStillRefusedInTheOther()
    {
        var provider = new GpuShaderCacheProvider(_environment);
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        var local = NvidiaRoot(ProfileArea.LocalAppData);
        var localLow = NvidiaRoot(ProfileArea.LocalLowAppData);

        // The premise, without which the assertion below proves nothing: this name really is
        // disposable in the other tier, so a policy keyed on the child name would allow both.
        Assert.True(policy.MayRemove(Path.Combine(local, "GLCache")).IsAllowed);

        Assert.False(policy.MayRemove(Path.Combine(localLow, "GLCache")).IsAllowed);
    }

    private string NvidiaRoot(ProfileArea area) =>
        GpuShaderCacheProvider.Roots
            .Single(root => root.DirectoryName == "NVIDIA" && root.Area == area)
            .PathIn(_environment)!;

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
    /// Firefox, Affinity and the Azure Functions tooling are deliberately absent. The sweep above probes a
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
        new PoetryCacheProvider(_environment),
        new GoCacheProvider(_environment),
        new VsCodeCppToolsCacheProvider(_environment),
        new DartAnalysisServerProvider(_environment),
        new RoslynCacheProvider(_environment),
        new PlaywrightBrowsersProvider(_environment),
        new GpuShaderCacheProvider(_environment),
        new EpicLauncherWebCacheProvider(_environment),
        new EpicLauncherLogProvider(_environment),

        // Its install directory is deliberately not registered here. The sweep probes ToolRoots[0],
        // which is Steam's folder in the profile and is declared from a constant; the install root
        // and the container under it have their own theory above, where the register is set up.
        new SteamCacheProvider(_environment),
        new SpotifyCacheProvider(_environment),
    ];

    /// <summary>
    /// The wiring, once, through a real provider: <see cref="ExploreActionPolicy.ForAsync"/> reads
    /// §5.2 out of the providers rather than restating it, so a provider's own declaration is what
    /// Explore enforces.
    /// </summary>
    [Fact]
    public async Task ThePolicyReadsSection52OutOfTheProvidersThemselves()
    {
        var provider = new GradleCacheProvider(_environment);
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.Equal(GradleRoot, provider.RootPath);
        Assert.False(policy.MayRemove(provider.RootPath).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(provider.RootPath, "gradle.properties")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(provider.RootPath, "caches")).IsAllowed);
    }

    /// <summary>
    /// The other half of that wiring, and the whole of issue #130: a root a provider can only name once it
    /// has asked the machine is enforced exactly as a declared one is.
    ///
    /// <para>The declared root allows <c>archive</c> and the discovered one does not know about it,
    /// so the two are told apart by more than their presence: a policy that merged the wrong way, or
    /// dropped either list, fails on one of the four assertions.</para>
    /// </summary>
    [Fact]
    public async Task ThePolicyEnforcesADiscoveredRootBesideADeclaredOne()
    {
        var declared = ToolRoot.Of(GradleRoot, "Gradle's own folder.", GradleCacheProvider.DisposableChildren);

        // Inside the profile, where the region table allows everything. Beside it, the table refuses
        // the folder as another account's, and the assertions below would pass with no declaration.
        var moved = Path.Combine(_environment.UserProfile, "moved-cache");

        var policy = await ExploreActionPolicy.ForAsync(
            _system,
            _environment,
            new FakeVolumeInventory(),
            [new StubProvider([declared], [VendorTool(moved)])]);

        Assert.True(policy.MayRemove(Path.Combine(GradleRoot, "caches")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GradleRoot, "gradle.properties")).IsAllowed);

        Assert.False(policy.MayRemove(moved).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(moved, "anything")).IsAllowed);
    }

    /// <summary>
    /// A tool's home at the top of a drive keeps its §5.2 protection. <c>CARGO_HOME</c> may name a
    /// drive root, and Cargo's registry tokens then sit directly under it, so the declaration there
    /// still refuses every child it does not recognise.
    /// </summary>
    [Fact]
    public void ADeclaredRootAtTheTopOfAVolumeStillRefusesItsUnrecognisedChildren()
    {
        var volume = Path.GetPathRoot(_temp.Path)!;
        var home = new ToolRoot(
            volume,
            "A tool's own folder, at the top of the drive.",
            static name => name.Equals("registry", StringComparison.OrdinalIgnoreCase));

        var policy = Policy(home);

        Assert.False(policy.MayRemove(Path.Combine(volume, "credentials.toml")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(volume, "registry")).IsAllowed);
    }

    /// <summary>
    /// A probed declaration only narrows. A root a setting produces, over the folder another
    /// declaration names and recognising a child that declaration refuses, must not open it:
    /// roots at one depth are pooled, and a setting can name anything, including the file that
    /// holds a tool's credentials.
    /// </summary>
    [Fact]
    public void AProbedRootCannotOpenWhatADeclaredRootRefuses()
    {
        var probed = new ToolRoot(
            GradleRoot,
            "A setting that names the file.",
            static name => name.Equals("gradle.properties", StringComparison.OrdinalIgnoreCase));

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment), [Gradle()], new FakeVolumeInventory(), probedRoots: [probed]);

        // The premise: on its own, the probed root allows the file, so the refusal below is the
        // declared root's and the probed root could not lift it.
        var alone = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment), [], new FakeVolumeInventory(), probedRoots: [probed]);

        Assert.True(alone.MayRemove(Path.Combine(GradleRoot, "gradle.properties")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GradleRoot, "gradle.properties")).IsAllowed);
    }

    /// <summary>
    /// The same rule one level down. The innermost roots decide, so a probed root inside a child the
    /// declared root refuses would otherwise answer for everything below it.
    /// </summary>
    [Fact]
    public void AProbedRootInsideARefusedChildCannotOpenIt()
    {
        var inside = Path.Combine(GradleRoot, "init.d");

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [Gradle()], new FakeVolumeInventory(),
            probedRoots: [new ToolRoot(inside, "A folder a setting names.", static _ => true)]);

        Assert.False(policy.MayRemove(Path.Combine(inside, "init.gradle")).IsAllowed);
    }

    /// <summary>
    /// A probed root that recognises every child refuses its own path and nothing inside it. That is
    /// how a provider declares a folder its plan protects, such as the one holding a relocated
    /// cache, without refusing what else the user keeps there.
    /// </summary>
    [Fact]
    public void AProbedRootRecognisingEveryChildRefusesOnlyItsOwnPath()
    {
        var container = Path.Combine(_environment.UserProfile, "build-tools");

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [], new FakeVolumeInventory(),
            probedRoots: [new ToolRoot(container, "A folder a plan protects.", static _ => true)]);

        Assert.False(policy.MayRemove(container).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(container, "other-tool")).IsAllowed);
    }

    /// <summary>
    /// Probed roots are not pooled with each other either. Two can land on one folder, as a vcpkg
    /// clone and a binary cache a variable put inside it both declare the clone, and the one that
    /// recognises every child must not lift the other's refusal.
    /// </summary>
    [Fact]
    public void AProbedRootCannotOpenWhatAnotherProbedRootRefuses()
    {
        var clone = Path.Combine(_environment.UserProfile, "vcpkg");

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [], new FakeVolumeInventory(),
            probedRoots:
            [
                new ToolRoot(
                    clone,
                    "The clone.",
                    static name => name.Equals("buildtrees", StringComparison.OrdinalIgnoreCase)),
                new ToolRoot(clone, "The folder holding a cache.", static _ => true),
            ]);

        Assert.False(policy.MayRemove(Path.Combine(clone, "installed")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(clone, "buildtrees")).IsAllowed);
    }

    /// <summary>
    /// The same rule one level down: a probed root inside a folder another probed root refuses
    /// outright does not answer for what is below it.
    /// </summary>
    [Fact]
    public void AProbedRootInsideAnotherCannotOpenWhatTheOuterRefuses()
    {
        var live = Path.Combine(_environment.UserProfile, "live-session");
        var inside = Path.Combine(live, "cache-holder");

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [], new FakeVolumeInventory(),
            probedRoots:
            [
                new ToolRoot(live, "Something is using this.", static _ => false),
                new ToolRoot(inside, "A folder holding a cache.", static _ => true),
            ]);

        Assert.False(policy.MayRemove(Path.Combine(inside, "anything")).IsAllowed);
    }

    /// <summary>
    /// What is inside a probed root that refuses it is refused with that root's own reason. The
    /// sentence for an unrecognised child of a tool's folder speaks of configuration beside a cache,
    /// which is untrue of a folder a program is using, and the user reading it is deciding whether to
    /// wait.
    /// </summary>
    [Fact]
    public void WhatIsInsideAProbedRootIsRefusedWithThatRootsReason()
    {
        var live = Path.Combine(_environment.UserProfile, "live-session");
        const string reason = "A running program is using this right now.";

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [], new FakeVolumeInventory(),
            probedRoots: [new ToolRoot(live, reason, static _ => false)]);

        var inside = policy.MayRemove(Path.Combine(live, "working.txt"));

        Assert.False(inside.IsAllowed);
        Assert.Contains(reason, inside.Reason, StringComparison.Ordinal);
        Assert.Equal(reason, policy.MayRemove(live).Reason);
    }

    private string GradleRoot => Path.Combine(_environment.UserProfile, ".gradle");

    private ToolRoot Gradle() =>
        ToolRoot.Of(GradleRoot, "Gradle's own folder.", GradleCacheProvider.DisposableChildren);

    private static ToolRoot VendorTool(string path) => new(path, "A vendor tool's own folder.", static _ => false);

    /// <summary>
    /// The region table this machine's fakes produce, with the roots handed straight to the
    /// constructor. <see cref="ExploreActionPolicy.ForAsync"/> is the wiring and is asserted on its
    /// own below; every rule here is about what the table and the declarations say, and routing each
    /// of them through a provider would make forty tests asynchronous to establish nothing.
    /// </summary>
    private ExploreActionPolicy Policy(params ToolRoot[] toolRoots) =>
        new(ProtectedRegions.For(_system, _environment), toolRoots, volumes: _volumes);

    private sealed class StubProvider(
        IReadOnlyList<ToolRoot> roots,
        IReadOnlyList<ToolRoot>? discovered = null) : ICleanupProvider
    {
        public string Id => "stub";

        public string Name => "Stub";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public Execution.StepGrain Grain => Execution.StepGrain.Parts;

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

        public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolRoot>>(discovered ?? []);

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public void InvalidateCaches()
        {
        }

        public Task<Execution.CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.CleanupResult> ExecuteAsync(
            Execution.CleanupPlan plan,
            Execution.RunReach? runReach = null,
            Execution.RunResidue? residue = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.VerificationResult> VerifyAsync(
            Execution.CleanupPlan plan,
            Execution.RunReach? runReach = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");
    }
}
