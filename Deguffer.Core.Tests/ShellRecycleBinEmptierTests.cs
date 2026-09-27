using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real emptier, with a recording stand-in for <c>SHEmptyRecycleBin</c> itself.
///
/// <para><b>Nothing here holds the native call, and nothing here may.</b> The call has no dry run,
/// and Microsoft documents a folder path as naming the bin of the drive it is on, and an empty path as
/// naming every bin on every drive. A test that held it would be one broken guard away from destroying
/// the deleted files of whoever ran the suite, and the guard is what these tests exist to break.
/// <see cref="TestAssemblySeamTests"/> keeps it that way.</para>
/// </summary>
public class ShellRecycleBinEmptierTests
{
    private readonly List<string> _asked = [];
    private readonly List<ApartmentState> _apartments = [];
    private int _answer;

    /// <summary>
    /// A path that is not a drive root is refused rather than tried, and the refusal is readable.
    ///
    /// <para>The case that makes it worth having is a derivation that has gone wrong: a bin laid out
    /// somewhere the two-levels-up rule does not describe. Without this, that would reach the shell,
    /// which would empty the bin of the drive the path is on and answer success.</para>
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\testuser")]
    [InlineData(@"C:\Users\testuser\$Recycle.Bin")]
    [InlineData(@"C:\Users\testuser\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData(@"C:")]
    [InlineData(@"..")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\server\share\")]
    public void RefusesAnythingThatIsNotADriveRoot(string path)
    {
        var outcome = Emptier().Empty(path);

        Assert.Empty(_asked);
        Assert.False(outcome.Emptied);
        Assert.Contains("not the root of a drive", outcome.Message);
        Assert.Contains(path, outcome.Message);
    }

    [Fact]
    public void RefusesAnEmptyPathOutright()
    {
        var emptier = Emptier();

        Assert.Throws<ArgumentException>(() => emptier.Empty("   "));
        Assert.Throws<ArgumentException>(() => emptier.Empty(string.Empty));
        Assert.Throws<ArgumentNullException>(() => emptier.Empty(null!));
        Assert.Empty(_asked);
    }

    /// <summary>
    /// The top of a drive goes to the shell exactly as given, in display form, once, and on a
    /// single-threaded apartment of its own.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"d:\")]
    public void AsksTheShellToEmptyTheTopOfADrive(string root)
    {
        var outcome = Emptier().Empty(root);

        Assert.Equal([root], _asked);
        Assert.Equal([ApartmentState.STA], _apartments);
        Assert.True(outcome.Emptied);
        Assert.Null(outcome.Message);
    }

    /// <summary>The shell's number goes to the user rather than being mapped to a guess.</summary>
    [Fact]
    public void ReportsARefusalByTheShellWithItsNumber()
    {
        _answer = unchecked((int)0x80070020);

        var outcome = Emptier().Empty(@"C:\");

        Assert.False(outcome.Emptied);
        Assert.Contains("0x80070020", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="IRecycleBinEmptier.Serves"/> decides a plan's route and <see cref="IRecycleBinEmptier.Empty"/>
    /// guards the call, so the two must give the same answer as the rule they share.
    /// </summary>
    [Theory]
    [MemberData(nameof(VolumeRootTests.DriveTops), MemberType = typeof(VolumeRootTests))]
    public void ServesExactlyTheTopOfADrive(string? path, bool driveTop)
    {
        Assert.Equal(driveTop, Emptier().Serves(path!));
        Assert.Empty(_asked);
    }

    private ShellRecycleBinEmptier Emptier() => new(root =>
    {
        _asked.Add(root);
        _apartments.Add(Thread.CurrentThread.GetApartmentState());
        return _answer;
    });
}
