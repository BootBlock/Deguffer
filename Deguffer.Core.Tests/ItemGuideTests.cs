using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The lookup itself, against entries this file writes rather than against the shipped catalogue.
///
/// <para>Two different things are being asserted and they fail for different reasons, so they are
/// asserted apart. What is here is the matching: which place an entry is measured from, which of
/// two overlapping entries wins, and what happens to a name nobody wrote about.
/// <see cref="KnownItemsTests"/> covers the catalogue's own content.</para>
///
/// <para>Everything runs against a synthetic Windows directory and a synthetic profile, which is
/// what <see cref="FakeSystemDirectories"/> and <see cref="FakeUserEnvironment"/> are for. A test
/// that only passed on a machine with a real <c>C:\Windows</c> would be asserting the machine.</para>
/// </summary>
public sealed class ItemGuideTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;

    public ItemGuideTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnOrdinaryPathIsNotDescribed()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, "WinSxS"));

        Assert.Null(guide.Describe(Path.Combine(_temp.Path, "holiday photos")));
    }

    [Fact]
    public void AnEntryUnderAPlaceIsFoundAtThatPlaceOnThisMachine()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, "WinSxS"));

        Assert.Equal(
            "WinSxS is what it is",
            guide.Describe(Path.Combine(_system.WindowsDirectory, "WinSxS"))?.Summary);
    }

    /// <summary>
    /// The place itself, which is how <c>C:\Windows</c> and <c>C:\ProgramData</c> are described.
    /// </summary>
    [Fact]
    public void AnEmptyRelativePathDescribesThePlaceItself()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, string.Empty));

        Assert.NotNull(guide.Describe(_system.WindowsDirectory));
    }

    /// <summary>
    /// A place is an address on this machine, not a name. The same leaf name somewhere else is a
    /// different thing and gets no explanation — which is the direction that matters, because the
    /// wrong one would tell somebody that a folder of theirs is part of Windows.
    /// </summary>
    [Fact]
    public void TheSameNameSomewhereElseIsNotDescribed()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, "WinSxS"));

        Assert.Null(guide.Describe(Path.Combine(_environment.UserProfile, "WinSxS")));
    }

    /// <summary>
    /// Several segments below a place, which is how a folder that only matters deep inside Windows
    /// is reached.
    /// </summary>
    [Fact]
    public void AnEntrySeveralSegmentsDownIsFound()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, @"System32\DriverStore\FileRepository"));

        Assert.NotNull(guide.Describe(
            Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository")));
    }

    /// <summary>
    /// The profile's own places are read through the seam rather than assembled from the profile
    /// path, because <c>%LOCALAPPDATA%</c> can be redirected out of the profile it is normally
    /// inside.
    /// </summary>
    [Theory]
    [InlineData(KnownPlace.UserProfile)]
    [InlineData(KnownPlace.LocalAppData)]
    [InlineData(KnownPlace.RoamingAppData)]
    public void EachProfilePlaceIsResolvedThroughItsOwnSeam(KnownPlace place)
    {
        var guide = Guide(At(place, "Anchored"));

        Assert.NotNull(guide.Describe(Path.Combine(Anchor(place), "Anchored")));
    }

    /// <summary>
    /// <c>C:\Users</c>, derived from the profile rather than assumed, so it is still right on a
    /// machine whose profiles have been moved.
    /// </summary>
    [Fact]
    public void TheProfilesFolderIsTheOneHoldingThisProfile()
    {
        var guide = Guide(At(KnownPlace.UserProfiles, string.Empty));

        Assert.NotNull(guide.Describe(Path.GetDirectoryName(_environment.UserProfile)!));
    }

    /// <summary>
    /// A volume-root entry is true of every volume, so it is matched by name at the top of whichever
    /// one the path is on. A drive mounted while the app is open must be covered exactly as one that
    /// was there at launch.
    /// </summary>
    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"Z:\pagefile.sys")]
    public void AVolumeRootEntryIsFoundOnEveryVolume(string path)
    {
        var guide = Guide(new KnownItem(KnownPlace.VolumeRoot, "pagefile.sys", "the paging file", "No."));

        Assert.NotNull(guide.Describe(path));
    }

    /// <summary>
    /// And nowhere else. A folder somebody called <c>pagefile.sys</c> inside their own documents is
    /// not the paging file, and saying it is would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void AVolumeRootEntryIsNotFoundDeeperDown()
    {
        var guide = Guide(new KnownItem(KnownPlace.VolumeRoot, "pagefile.sys", "the paging file", "No."));

        Assert.Null(guide.Describe(@"C:\Users\testuser\pagefile.sys"));
    }

    [Fact]
    public void AnAnywhereEntryIsFoundAtAnyDepth()
    {
        var guide = Guide(new KnownItem(KnownPlace.Anywhere, "node_modules", "packages", "Yes."));

        Assert.NotNull(guide.Describe(@"C:\Users\testuser\code\site\node_modules"));
        Assert.NotNull(guide.Describe(@"D:\node_modules"));
    }

    /// <summary>
    /// Where both could answer, the one written about this exact address wins. The anchored entry is
    /// about this thing; the name matched anywhere is about a name.
    /// </summary>
    [Fact]
    public void AnAnchoredEntryBeatsANameMatchedAnywhere()
    {
        var guide = Guide(
            At(KnownPlace.LocalAppData, "Temp"),
            new KnownItem(KnownPlace.Anywhere, "Temp", "any temp folder", "Yes."));

        Assert.Equal(
            "Temp is what it is",
            guide.Describe(Path.Combine(_environment.LocalAppData, "Temp"))?.Summary);
    }

    /// <summary>
    /// Matching is case-insensitive and goes through <c>LongPath.Configured</c>, because the paths
    /// arrive from a scan rather than from this file. NTFS does not tell two spellings apart, and a
    /// path carrying <c>..</c> compares equal to nothing at all.
    /// </summary>
    [Fact]
    public void APathIsNormalisedAndComparedWithoutCase()
    {
        var guide = Guide(At(KnownPlace.WindowsDirectory, "WinSxS"));

        var awkward = Path.Combine(_system.WindowsDirectory, "System32", "..", "winsxs");

        Assert.NotNull(guide.Describe(awkward));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"relative\path")]
    public void SomethingThatIsNotAPathIsNotDescribed(string? path)
    {
        Assert.Null(Guide(At(KnownPlace.WindowsDirectory, "WinSxS")).Describe(path));
    }

    /// <summary>
    /// A place that names no directory contributes nothing. <c>%ProgramFiles(x86)%</c> is genuinely
    /// empty on a 32-bit Windows, and an empty anchor combined with a relative path would build a
    /// key that matches something nobody meant.
    /// </summary>
    [Fact]
    public void APlaceThatNamesNoDirectoryContributesNothing()
    {
        var guide = new ItemGuide(
            [At(KnownPlace.ProgramFilesX86, "Common Files")],
            new Dictionary<KnownPlace, string> { [KnownPlace.ProgramFilesX86] = string.Empty });

        Assert.Null(guide.Describe(@"C:\Program Files (x86)\Common Files"));
    }

    /// <summary>An entry whose place was never resolved is simply absent, rather than throwing.</summary>
    [Fact]
    public void AnEntryWithNoAnchorAtAllIsAbsent()
    {
        var guide = new ItemGuide([At(KnownPlace.ProgramData, "Package Cache")], new Dictionary<KnownPlace, string>());

        Assert.Null(guide.Describe(@"C:\ProgramData\Package Cache"));
    }

    private ItemGuide Guide(params KnownItem[] entries) =>
        new(entries, Anchors());

    private Dictionary<KnownPlace, string> Anchors() => new()
    {
        [KnownPlace.WindowsDirectory] = _system.WindowsDirectory,
        [KnownPlace.ProgramFiles] = _system.ProgramFiles,
        [KnownPlace.ProgramFilesX86] = _system.ProgramFilesX86,
        [KnownPlace.ProgramData] = _system.ProgramData,
        [KnownPlace.UserProfiles] = Path.GetDirectoryName(_environment.UserProfile)!,
        [KnownPlace.UserProfile] = _environment.UserProfile,
        [KnownPlace.LocalAppData] = _environment.LocalAppData,
        [KnownPlace.RoamingAppData] = _environment.RoamingAppData,
    };

    private string Anchor(KnownPlace place) => Anchors()[place];

    /// <summary>An entry whose text says which entry it is, so a match can be told from a near miss.</summary>
    private static KnownItem At(KnownPlace place, string relative)
    {
        var name = relative.Length == 0 ? "the place" : relative[(relative.LastIndexOf('\\') + 1)..];

        return new KnownItem(place, relative, $"{name} is what it is", "It cannot be deleted.");
    }
}
