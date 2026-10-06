using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What Windows keeps at the top of a volume that Explore refuses, and the agreement between that
/// refusal and what the guide tells a reader about the same names.
///
/// <para>Asserted on <c>Q:\</c>, a drive the region table knows nothing about, so what is being
/// measured is this rule and not another one. The synthetic profile lives under the temp directory,
/// and everything beside it there is already refused as another account's.</para>
/// </summary>
public sealed class VolumeReservationTests : IDisposable
{
    /// <summary>
    /// The names the guide describes at a volume root that Explore allows, each on purpose. None of
    /// the guide's entries for them says the machine cannot start or repair itself without it.
    /// <c>inetpub</c> and <c>AMD</c> are allowed here because what the guide says must stay is on the
    /// drive Windows is installed on, and a rule for that drive refuses it there:
    /// <see cref="InetpubIsRefusedOnTheSystemDriveAndWhatIsInsideItIsNot"/> and
    /// <see cref="AmdIsRefusedOnTheSystemDriveExceptTheDriverPackagesInsideIt"/>.
    /// </summary>
    private static readonly HashSet<string> AllowedOnPurpose = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.old",
        "$WinREAgent",
        "$Windows.~BT",
        "Config.Msi",
        "PerfLogs",
        "MSOCache",
        "inetpub",
        "AMD",
    };

    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public VolumeReservationTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The boot files, the recovery environment and the signpost to the Users folder. Each was
    /// answered as unclassified and offered for removal, while the guide said the machine needs it.
    /// </summary>
    [Theory]
    [InlineData("bootmgr")]
    [InlineData("BOOTMGR")]
    [InlineData("Boot")]
    [InlineData("Recovery")]
    [InlineData("Documents and Settings")]
    public void WhatTheMachineNeedsAtAVolumeRootIsRefusedOnAnyDrive(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\", name)).IsAllowed);
    }

    /// <summary>
    /// And everything inside them, because removing a folder removes what is in it. The boot
    /// configuration and the recovery image are one level down, and they are what the machine needs.
    /// </summary>
    [Theory]
    [InlineData(@"Boot\BCD")]
    [InlineData(@"Boot\en-US\bootmgr.exe.mui")]
    [InlineData(@"Recovery\WindowsRE\Winre.wim")]
    [InlineData(@"RECOVERY\WindowsRE")]
    [InlineData(@"System Volume Information\tracking.log")]
    public void EverythingInsideThemIsRefused(string relative)
    {
        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\", relative)).IsAllowed);
    }

    /// <summary>
    /// On a volume mounted at a folder, whose top is that folder.
    /// </summary>
    [Theory]
    [InlineData("bootmgr")]
    [InlineData(@"Boot\BCD")]
    [InlineData(@"Recovery\WindowsRE")]
    public void TheyAreRefusedAtTheTopOfAVolumeMountedAtAFolder(string relative)
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        Assert.False(Policy().MayRemove(Path.Combine(@"Q:\Mount", relative)).IsAllowed);
    }

    /// <summary>
    /// The §5.6 half. The rule is about the place at the top of a volume, not the word: a folder
    /// somebody called <c>Recovery</c> inside their own files is theirs, and so is a name that only
    /// starts like one of these.
    /// </summary>
    [Theory]
    [InlineData(@"Q:\Photos\Recovery")]
    [InlineData(@"Q:\Photos\Recovery\img_0001.jpg")]
    [InlineData(@"Q:\Games\Boot")]
    [InlineData(@"Q:\Bootleg")]
    [InlineData(@"Q:\Recovery.old")]
    [InlineData(@"Q:\bootmgr.bak")]
    [InlineData(@"Q:\Mount\Holiday photos\Boot")]
    public void TheSameNamesElsewhereAreOrdinary(string path)
    {
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [@"Q:\Mount\"]);

        Assert.True(Policy().MayRemove(path).IsAllowed);
    }

    /// <summary>
    /// The guide and the policy agree about every name at the top of a volume. The guide explains
    /// and decides nothing, so a new entry saying the machine needs an item would otherwise be a
    /// promise the policy does not keep, and the confirmation would then say Deguffer has not
    /// classified it. Every entry is refused, or listed in <see cref="AllowedOnPurpose"/> by a
    /// decision somebody made.
    /// </summary>
    [Fact]
    public void EveryNameTheGuideDescribesAtAVolumeRootIsRefusedOrAllowedOnPurpose()
    {
        var policy = Policy();

        var disagreements = KnownItems.All
            .Where(item => item.Place == KnownPlace.VolumeRoot)
            .Select(item => item.RelativePath)
            .Where(name =>
                policy.MayRemove(Path.Combine(@"Q:\", name)).IsAllowed != AllowedOnPurpose.Contains(name))
            .ToList();

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// §7.1 refuses every path a provider names as protected, and <see cref="SystemDriveRoot"/> names
    /// what Windows keeps at the top of its own drive as §5.6 survivors. The drive is a volume of its
    /// own here, with the profile inside it where Windows puts it, so each survivor is answered by the
    /// rule that covers it on a real machine rather than as another account's folder.
    /// </summary>
    [Fact]
    public void EverySurvivorAtTheTopOfTheSystemDriveIsRefused()
    {
        using var drive = new TempDirectory();
        var system = new FakeSystemDirectories(drive.Path);
        var environment = new FakeUserEnvironment(Path.Combine(drive.Path, "Users"));
        _volumes.With(@"R:\", alsoMountedAt: [drive.Path + Path.DirectorySeparatorChar]);

        var policy = new ExploreActionPolicy(ProtectedRegions.For(system, environment), [], _volumes);

        var allowed = SystemDriveRoot.Survivors
            .Select(survivor => survivor.RelativePath)
            .Where(name => policy.MayRemove(Path.Combine(system.SystemDrive, name)).IsAllowed)
            .ToList();

        Assert.Empty(allowed);
    }

    /// <summary>
    /// Windows Update's fix is the folder's existence and permissions at the top of its own drive, so
    /// the folder is refused there. What a web server keeps inside it is that server's content, and
    /// an <c>inetpub</c> on another drive, or one somebody keeps among their own files, is not the
    /// fix at all.
    /// </summary>
    [Fact]
    public void InetpubIsRefusedOnTheSystemDriveAndWhatIsInsideItIsNot()
    {
        using var drive = new TempDirectory();
        var system = new FakeSystemDirectories(drive.Path);
        var environment = new FakeUserEnvironment(Path.Combine(drive.Path, "Users"));
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [drive.Path + Path.DirectorySeparatorChar]);

        var policy = new ExploreActionPolicy(ProtectedRegions.For(system, environment), [], _volumes);

        Assert.False(policy.MayRemove(Path.Combine(system.SystemDrive, "inetpub")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(system.SystemDrive, "INETPUB")).IsAllowed);

        Assert.True(policy.MayRemove(Path.Combine(system.SystemDrive, "inetpub", "logs")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(system.SystemDrive, "inetpub", "wwwroot", "index.html")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(system.SystemDrive, "inetpub.old")).IsAllowed);
        Assert.True(policy.MayRemove(@"Q:\inetpub").IsAllowed);
    }

    /// <summary>
    /// The guide says the AMD folder must stay for the chipset driver's install source, and that the
    /// graphics driver packages inside it can go. On the drive Windows is installed on, the graphics
    /// driver installer provider's own §5.2 declaration says exactly that, so no volume rule is
    /// written over it: one would also refuse the packages it removes. Anything else in the folder is
    /// unrecognised and refused, and an <c>AMD</c> on another drive is not a folder Deguffer
    /// recognises, so it is ordinary there.
    /// </summary>
    [Fact]
    public void AmdIsRefusedOnTheSystemDriveExceptTheDriverPackagesInsideIt()
    {
        using var drive = new TempDirectory();
        var system = new FakeSystemDirectories(drive.Path);
        var environment = new FakeUserEnvironment(Path.Combine(drive.Path, "Users"));
        _volumes.With(@"Q:\").With(@"R:\", alsoMountedAt: [drive.Path + Path.DirectorySeparatorChar]);

        var amd = Path.Combine(system.SystemDrive, "AMD");
        Directory.CreateDirectory(Path.Combine(amd, "Chipset_Software", "Packages"));
        Directory.CreateDirectory(Path.Combine(amd, "AMD-Software-Installer"));
        Directory.CreateDirectory(Path.Combine(amd, "Radeon Recordings"));

        var provider = new GraphicsDriverInstallerProvider(environment, new FakeProcessRunner(), system: system);
        var policy = new ExploreActionPolicy(ProtectedRegions.For(system, environment), provider.ToolRoots, _volumes);

        Assert.False(policy.MayRemove(amd).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(amd, "Chipset_Software")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(amd, "Chipset_Software", "Packages")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(amd, "Radeon Recordings")).IsAllowed);

        Assert.True(policy.MayRemove(Path.Combine(amd, "AMD-Software-Installer")).IsAllowed);
        Assert.True(policy.MayRemove(@"Q:\AMD").IsAllowed);
    }

    private ExploreActionPolicy Policy() =>
        new(ProtectedRegions.For(_system, _environment), [], _volumes);
}
