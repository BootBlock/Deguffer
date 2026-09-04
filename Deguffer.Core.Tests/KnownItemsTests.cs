using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The catalogue's own content, as against <see cref="ItemGuideTests"/>, which covers the matching.
///
/// <para>Every entry here is a claim Deguffer makes to somebody about their own disk, and the last
/// line of each is a claim about whether deleting something recovers space. Nothing in a test can
/// check that a sentence is <em>true</em> — that is what the sourcing behind each entry is for — so
/// what is checked here is everything that can be: that each entry answers the question at all,
/// that it answers it in the shape the page lays out, and that every entry is reachable on a
/// machine.</para>
/// </summary>
public sealed class KnownItemsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;

    public KnownItemsTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    public static TheoryData<KnownItem> Everything()
    {
        var data = new TheoryData<KnownItem>();

        foreach (var entry in KnownItems.All)
        {
            data.Add(entry);
        }

        return data;
    }

    [Fact]
    public void TheCatalogueIsNotEmpty() => Assert.NotEmpty(KnownItems.All);

    [Theory]
    [MemberData(nameof(Everything))]
    public void EveryEntrySaysWhatTheThingIs(KnownItem entry)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
        Assert.EndsWith(".", entry.Summary.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The deletion verdict is the reason the reader stopped on the row, so every entry has to
    /// answer it. An entry that explains what something is and goes quiet about whether it can go
    /// leaves them exactly where they started.
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void EveryEntrySaysWhetherDeletingItRecoversAnything(KnownItem entry)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.Removal));
        Assert.EndsWith(".", entry.Removal.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// And says it on one line. The page puts that line last with an empty line above it so it
    /// cannot be skimmed past, and a verdict that wrapped itself over two lines of its own would
    /// take that apart.
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void TheVerdictIsOneLine(KnownItem entry)
    {
        Assert.DoesNotContain('\n', entry.Removal);
        Assert.DoesNotContain('\r', entry.Removal);
    }

    /// <summary>
    /// A relative path is a path below its place, so it neither starts at a root nor carries a
    /// separator at either end — <see cref="Path.Combine(string, string)"/> discards the anchor
    /// outright for a rooted second argument, which would put the entry at an address on some other
    /// machine's disk.
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void EveryRelativePathIsRelative(KnownItem entry)
    {
        Assert.False(Path.IsPathRooted(entry.RelativePath));
        Assert.Equal(entry.RelativePath.Trim('\\', '/'), entry.RelativePath);
    }

    /// <summary>
    /// A name matched anywhere is matched on the leaf alone, so an entry with more than one segment
    /// in it would never be found at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void ANameMatchedByItsLeafIsOneSegment(KnownItem entry)
    {
        if (entry.Place is KnownPlace.Anywhere)
        {
            Assert.DoesNotContain('\\', entry.RelativePath);
            Assert.NotEmpty(entry.RelativePath);
        }
    }

    /// <summary>
    /// A volume-root entry names something <em>on</em> the volume, so an empty relative path there
    /// would be the drive itself — which is not a thing to explain or to remove.
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void AVolumeRootEntryNamesSomethingOnTheVolume(KnownItem entry)
    {
        if (entry.Place is KnownPlace.VolumeRoot)
        {
            Assert.NotEmpty(entry.RelativePath);
        }
    }

    /// <summary>
    /// Every entry is reachable through the lookup at the address its place implies.
    ///
    /// <para>This is what catches the entry two people wrote about one directory from different
    /// places — <c>%USERPROFILE%\AppData\Local\Temp</c> and <c>%LOCALAPPDATA%\Temp</c> are one
    /// folder — where the second silently replaces the first in the lookup and the catalogue goes
    /// on listing both. It also catches a place whose anchor was never wired up.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Everything))]
    public void EveryEntryIsFoundWhereItSaysItIs(KnownItem entry)
    {
        Assert.Same(entry, ItemGuide.For(_system, _environment).Describe(Address(entry)));
    }

    /// <summary>
    /// The catalogue against a real machine's directories, which is the one arrangement the app
    /// actually runs in. It asserts the wiring rather than the text: that
    /// <see cref="ItemGuide.ForThisMachine"/> resolves its anchors and finds something through them.
    /// </summary>
    [Fact]
    public void TheCatalogueResolvesAgainstThisMachine()
    {
        var guide = ItemGuide.ForThisMachine();

        Assert.NotNull(guide.Describe(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(guide.Describe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    /// <summary>
    /// The tooltip's shape, which is the whole of what the reader sees: the explanation, then the
    /// page's own facts, then the verdict alone under an empty line.
    /// </summary>
    [Fact]
    public void TheVerdictEndsTheTooltipUnderAnEmptyLine()
    {
        var entry = new KnownItem(KnownPlace.VolumeRoot, "x", "What it is.", "It cannot go.");

        Assert.Equal("What it is.\r\n\r\nCreated: today\r\n\r\nIt cannot go.", entry.Tip("Created: today"));
    }

    /// <summary>
    /// And the same shape with nothing in the middle, which is the map: the size and the date are
    /// already on the status line under the picture.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheVerdictStillEndsItWhereThePageAddedNothing(string? facts)
    {
        var entry = new KnownItem(KnownPlace.VolumeRoot, "x", "What it is.", "It cannot go.");

        Assert.Equal("What it is.\r\n\r\nIt cannot go.", entry.Tip(facts));
    }

    /// <summary>Where <paramref name="entry"/> sits, worked out here rather than asked of the lookup.</summary>
    private string Address(KnownItem entry) => entry.Place switch
    {
        KnownPlace.VolumeRoot => Path.Combine(@"C:\", entry.RelativePath),
        KnownPlace.Anywhere => Path.Combine(@"C:\somewhere\else", entry.RelativePath),
        _ => Path.Combine(Anchor(entry.Place), entry.RelativePath),
    };

    private string Anchor(KnownPlace place) => place switch
    {
        KnownPlace.WindowsDirectory => _system.WindowsDirectory,
        KnownPlace.ProgramFiles => _system.ProgramFiles,
        KnownPlace.ProgramFilesX86 => _system.ProgramFilesX86,
        KnownPlace.ProgramData => _system.ProgramData,
        KnownPlace.UserProfiles => Path.GetDirectoryName(_environment.UserProfile)!,
        KnownPlace.UserProfile => _environment.UserProfile,
        KnownPlace.LocalAppData => _environment.LocalAppData,
        KnownPlace.RoamingAppData => _environment.RoamingAppData,
        _ => throw new ArgumentOutOfRangeException(nameof(place), place, "No anchor for this place."),
    };
}
