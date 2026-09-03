using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What survives an elevated relaunch.
///
/// <para>The process that stands down takes everything it knew with it, so the command line is the
/// only channel between the two instances. Both ends live in <see cref="ElevationRequest"/> for that
/// reason, and these tests are what hold them to each other: a switch that is written one way and
/// read another produces no error at all, just a window that opens somewhere the user did not leave
/// it.</para>
///
/// <para>Proved here rather than through the shell because none of it needs a window, and the shell
/// has no test project (G8).</para>
/// </summary>
public sealed class ElevationRequestTests
{
    private const string Drive = @"D:\";

    private const string Folder = @"C:\Users\testuser\source\my project";

    /// <summary>
    /// The Storage page's relaunch has to behave exactly as it did before Explore joined it, and
    /// the argument it sends is the whole of that behaviour. Pinned rather than round-tripped: a
    /// renamed switch round-trips perfectly and still strands anyone mid-upgrade.
    /// </summary>
    [Fact]
    public void APreviewAsksForTheSwitchTheStoragePageAlreadyUsed()
    {
        Assert.Equal(["--rescan"], ElevationRequest.Preview.ToArguments());
    }

    [Fact]
    public void APreviewComesBackAsOne()
    {
        Assert.IsType<PreviewRequest>(ElevationRequest.From(ElevationRequest.Preview.ToArguments()));
    }

    [Fact]
    public void ADriveTravels()
    {
        var restored = RoundTrip(new ExploreRequest(Drive, Folder: null));

        Assert.Equal(Drive, restored.Drive);
        Assert.Null(restored.Folder);
    }

    /// <summary>
    /// Both halves, because the drive box and the folder beside it state one choice between them.
    /// Restoring the folder alone leaves the box naming whichever volume it defaulted to.
    /// </summary>
    [Fact]
    public void AFolderTravelsWithTheDriveItWasChosenOn()
    {
        var restored = RoundTrip(new ExploreRequest(@"C:\", Folder));

        Assert.Equal(@"C:\", restored.Drive);
        Assert.Equal(Folder, restored.Folder);
    }

    /// <summary>
    /// A path with a space in it is one argument. This is what would break first if the two values
    /// were ever joined into a single string, and a truncated path is a scan of the wrong folder.
    /// </summary>
    [Fact]
    public void APathWithSpacesInItArrivesWhole()
    {
        Assert.Equal(Folder, RoundTrip(new ExploreRequest(null, Folder)).Folder);
    }

    /// <summary>
    /// The dangerous direction. An Explore relaunch that also carried the Storage switch would
    /// start a whole-machine preview behind the page the user is watching.
    /// </summary>
    [Fact]
    public void AnExploreRequestNeverAlsoAsksForAPreview()
    {
        Assert.DoesNotContain("--rescan", new ExploreRequest(Drive, Folder).ToArguments());
    }

    /// <summary>
    /// Neither path is set, which leaves nothing for the drive and folder switches to carry. It is
    /// still a request to open on Explore, and decoding it as no request at all would land the user
    /// on the page they did not leave.
    /// </summary>
    [Fact]
    public void AnExploreRequestWithNothingToCarryIsStillOne()
    {
        var restored = RoundTrip(new ExploreRequest(null, null));

        Assert.Null(restored.Drive);
        Assert.Null(restored.Folder);
    }

    /// <summary>
    /// An empty value is a missing path rather than a path. Taking it literally would point a scan
    /// at the empty string.
    /// </summary>
    [Fact]
    public void AnEmptyValueIsNoPathAtAll()
    {
        var restored = Assert.IsType<ExploreRequest>(
            ElevationRequest.From(["--explore", "--explore-drive=", "--explore-folder="]));

        Assert.Null(restored.Drive);
        Assert.Null(restored.Folder);
    }

    /// <summary>
    /// The ordinary case: a user starting Deguffer passes none of this, and every one of these
    /// arguments must leave them on the default page rather than resuming somebody's scan.
    /// </summary>
    [Theory]
    [InlineData(@"--explore-drive=D:\")]
    [InlineData(@"--explore-folder=C:\Users\testuser")]
    [InlineData("/rescan", "-explore", "explore")]
    public void ALaunchThatAsksForNothingGetsNothing(params string[] arguments)
    {
        Assert.Null(ElevationRequest.From(arguments));
    }

    /// <summary>The commonest launch of all: the user opened Deguffer themselves.</summary>
    [Fact]
    public void ALaunchWithNoArgumentsAsksForNothing()
    {
        Assert.Null(ElevationRequest.From([]));
    }

    /// <summary>
    /// A shell may hand an argument back in any case, and a request read as no request is a silent
    /// failure rather than a reported one.
    /// </summary>
    [Fact]
    public void TheSwitchesAreReadWhateverTheirCase()
    {
        Assert.IsType<PreviewRequest>(ElevationRequest.From(["--RESCAN"]));

        Assert.Equal(
            Drive,
            Assert.IsType<ExploreRequest>(
                ElevationRequest.From(["--Explore", "--EXPLORE-DRIVE=" + Drive])).Drive);
    }

    /// <summary>An argument Deguffer does not know is not a reason to ignore the ones it does.</summary>
    [Fact]
    public void ArgumentsItDoesNotKnowAreSteppedOver()
    {
        Assert.IsType<ExploreRequest>(
            ElevationRequest.From(["--verbose", "--explore", @"C:\some\stray\path"]));
    }

    private static ExploreRequest RoundTrip(ExploreRequest request) =>
        Assert.IsType<ExploreRequest>(ElevationRequest.From(request.ToArguments()));
}
