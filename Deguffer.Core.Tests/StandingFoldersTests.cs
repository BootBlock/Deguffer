using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one answer to "may this folder be taken whole?" that every setting and every removal asks.
/// The case that made it one answer was a vcpkg or Maven setting naming Downloads, the Desktop or
/// Documents as a cache: nothing refused the account's own folders, so the folder became a target.
/// </summary>
public sealed class StandingFoldersTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public StandingFoldersTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "machine"));
    }

    public void Dispose() => _temp.Dispose();

    private string? WhyNotTaken(string path) => StandingFolders.WhyNotTaken(path, _environment, _system);

    [Theory]
    [InlineData("Desktop")]
    [InlineData("Documents")]
    [InlineData("Downloads")]
    [InlineData("Music")]
    [InlineData("Pictures")]
    [InlineData("Videos")]
    [InlineData("Saved Games")]
    [InlineData("OneDrive")]
    public void RefusesEachOfTheAccountsOwnFolders(string name)
    {
        var why = WhyNotTaken(Path.Combine(_environment.UserProfile, name));

        Assert.Equal("it is one of your own folders, where you keep your files.", why);
    }

    /// <summary>
    /// Windows and OneDrive both move these folders, and a setting naming the moved one names the
    /// account's files as surely as one naming the default. The default place is still refused as
    /// well, because Windows' answer is not the only folder of that name the account has.
    /// </summary>
    [Fact]
    public void RefusesAFolderWindowsHasMovedAndTheFolderHoldingIt()
    {
        var moved = Path.Combine(_temp.Path, "data", "Documents");
        var environment = new FakeUserEnvironment(Path.Combine(_temp.Path, "other")).WithNoDocuments().WithPersonalFolderAt(moved);

        Assert.NotNull(StandingFolders.WhyNotTaken(moved, environment, _system));
        Assert.Contains(
            "one of your own folders",
            StandingFolders.WhyNotTaken(Path.Combine(_temp.Path, "data"), environment, _system),
            StringComparison.Ordinal);
        Assert.NotNull(StandingFolders.WhyNotTaken(Path.Combine(environment.UserProfile, "Documents"), environment, _system));
    }

    /// <summary>
    /// A folder somebody chose to keep a cache in is theirs to choose. Whether what is in it is the
    /// tool's is the provider's evidence to establish, and refusing it here would refuse every cache
    /// kept in Documents.
    /// </summary>
    [Fact]
    public void LeavesAFolderInsideOneOfTheAccountsOwnFoldersToTheProvider()
    {
        var inside = Path.Combine(_environment.UserProfile, "Downloads", "vcpkg-archives");

        Assert.Null(WhyNotTaken(inside));
        Assert.Equal(
            Path.Combine(_environment.UserProfile, "Downloads"),
            StandingFolders.PersonalFolderHolding(inside, _environment));
    }

    /// <summary>
    /// A path from a step is in the extended form and may end in a separator, and one from the
    /// environment is in neither. Compared as they arrive, the two would never match and the check
    /// would pass the folder it exists to refuse.
    /// </summary>
    [Fact]
    public void RecognisesTheFolderInTheExtendedFormAndWithATrailingSeparator()
    {
        var downloads = Path.Combine(_environment.UserProfile, "Downloads");

        Assert.StartsWith(@"\\?\", LongPath.Extended(downloads), StringComparison.Ordinal);
        Assert.NotNull(WhyNotTaken(LongPath.Extended(downloads)));
        Assert.NotNull(WhyNotTaken(downloads + Path.DirectorySeparatorChar));
        Assert.NotNull(WhyNotTaken(LongPath.Extended(downloads) + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RefusesADriveRoot()
    {
        Assert.Equal("it is the root of a drive or a share.", WhyNotTaken(@"D:\"));
        Assert.Equal("it is the root of a drive or a share.", WhyNotTaken(@"\\?\D:\"));
    }

    /// <summary>
    /// A folder holding the profile holds every one of the account's folders too, and the profile is
    /// the more useful thing to name.
    /// </summary>
    [Fact]
    public void NamesTheProfileRatherThanAFolderInsideItForAFolderHoldingIt()
    {
        var why = WhyNotTaken(_temp.Path);

        Assert.Contains("your profile", why, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTheApplicationDataTiersAndTheFoldersWindowsIsBuiltOutOf()
    {
        foreach (var folder in new[]
        {
            _environment.UserProfile,
            _environment.LocalAppData,
            _environment.RoamingAppData,
            _environment.LocalLowAppData!,
            _system.WindowsDirectory,
            _system.ProgramData,
            _system.ProgramFiles,
            _system.ProgramFilesX86,
        })
        {
            Assert.NotNull(WhyNotTaken(folder));
        }
    }

    [Fact]
    public void LeavesAnOrdinaryCacheFolderAlone()
    {
        Assert.Null(WhyNotTaken(Path.Combine(_environment.LocalAppData, "vcpkg", "archives")));
        Assert.Null(WhyNotTaken(Path.Combine(_temp.Path, "shared", "m2-repository")));
    }
}
