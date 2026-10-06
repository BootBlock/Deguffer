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

    /// <summary>
    /// What the machine has mounted. Empty unless a test mounts something, which is the machine
    /// naming no other path to anything, so the rest of these read a path's own text.
    /// </summary>
    private readonly FakeVolumeInventory _volumes = new();

    public StandingFoldersTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "machine"));
    }

    public void Dispose() => _temp.Dispose();

    private string? WhyNotTaken(string path) => StandingFolders.WhyNotTaken(path, _environment, _system, _volumes);

    private string? PersonalFolderHolding(string path) =>
        StandingFolders.Examine(ReachedFolder.At(path, _volumes), _environment, _system).PersonalFolder;

    /// <summary>The folder at <paramref name="path"/>, reached below <paramref name="mountPoint"/> rather than its own root.</summary>
    private static string Through(string mountPoint, string path) =>
        Path.Join(mountPoint, path[Path.GetPathRoot(path)!.Length..]);

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

        Assert.NotNull(StandingFolders.WhyNotTaken(moved, environment, _system, _volumes));
        Assert.Contains(
            "one of your own folders",
            StandingFolders.WhyNotTaken(Path.Combine(_temp.Path, "data"), environment, _system, _volumes),
            StringComparison.Ordinal);
        Assert.NotNull(
            StandingFolders.WhyNotTaken(Path.Combine(environment.UserProfile, "Documents"), environment, _system, _volumes));
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
        Assert.Equal(Path.Combine(_environment.UserProfile, "Downloads"), PersonalFolderHolding(inside));
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

    /// <summary>
    /// A path may name the folder in any device-namespace spelling Windows accepts. Before they were
    /// read as the folder they name, <c>//?/</c>, <c>\\.\</c> and <c>\??\</c> compared equal to
    /// nothing here, and <see cref="LongPath.Configured"/> handed <c>//?/</c> on still prefixed, so
    /// any comparison made without <see cref="LongPath.Display"/> missed it.
    /// </summary>
    [Fact]
    public void RecognisesTheFolderInEveryDeviceSpelling()
    {
        var downloads = Path.Combine(_environment.UserProfile, "Downloads");

        Assert.NotNull(WhyNotTaken(@"\\.\" + downloads));
        Assert.NotNull(WhyNotTaken(@"\??\" + downloads));
        Assert.NotNull(WhyNotTaken("//?/" + downloads.Replace('\\', '/')));
        Assert.Equal(downloads, LongPath.Configured("//?/" + downloads.Replace('\\', '/')));
    }

    [Fact]
    public void RefusesADriveRoot()
    {
        Assert.Equal("it is the root of a drive, a share or a volume.", WhyNotTaken(@"D:\"));
        Assert.Equal("it is the root of a drive, a share or a volume.", WhyNotTaken(@"\\?\D:\"));
    }

    /// <summary>
    /// A folder a volume is mounted at is the top of that volume, and taking it takes everything on
    /// the volume. Its text names a folder like any other, so only asking the machine finds it.
    /// </summary>
    [Fact]
    public void RefusesAFolderAVolumeIsMountedAt()
    {
        _volumes.With(Path.GetPathRoot(_temp.Path)!, alsoMountedAt: [@"Q:\SysMount\"]);

        Assert.Equal("it is the root of a drive, a share or a volume.", WhyNotTaken(@"Q:\SysMount"));
        Assert.Equal("it is the root of a drive, a share or a volume.", WhyNotTaken(@"\\?\Q:\SysMount\"));
        Assert.Null(WhyNotTaken(@"Q:\Elsewhere"));
    }

    /// <summary>
    /// The profile's volume also mounted at a folder puts every one of the account's folders there
    /// too. Asked about that path's text alone, nothing here recognised them, and removing one
    /// removes the account's files. An ordinary folder reached the same way stays removable (§5.6),
    /// and so does a folder beside the mount point, which is on another volume.
    /// </summary>
    [Fact]
    public void RefusesTheAccountsFoldersReachedThroughAnotherMountOfTheirVolume()
    {
        _volumes.With(Path.GetPathRoot(_temp.Path)!, alsoMountedAt: [@"Q:\SysMount\"]);
        var downloads = Through(@"Q:\SysMount\", Path.Combine(_environment.UserProfile, "Downloads"));

        Assert.Equal("it is one of your own folders, where you keep your files.", WhyNotTaken(downloads));
        Assert.Equal(
            "it is one of your own folders, where you keep your files.",
            WhyNotTaken(LongPath.Extended(downloads)));
        Assert.Contains(
            "your profile", WhyNotTaken(Through(@"Q:\SysMount\", _environment.LocalAppData)), StringComparison.Ordinal);
        Assert.Contains("your profile", WhyNotTaken(Through(@"Q:\SysMount\", _temp.Path)), StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(_environment.UserProfile, "Downloads"),
            PersonalFolderHolding(Path.Combine(downloads, "vcpkg-archives")));

        var ordinary = Through(@"Q:\SysMount\", Path.Combine(_temp.Path, "shared", "m2-repository"));
        Assert.Null(WhyNotTaken(ordinary));
        Assert.Null(PersonalFolderHolding(ordinary));
        Assert.Null(WhyNotTaken(@"Q:\Downloads"));
    }

    /// <summary>
    /// A letter <c>subst</c> made for the profile reaches every folder in it, and Windows names no
    /// volume for the letter, so no mount point leads back. Followed to the folder it stands for,
    /// <c>S:\Downloads</c> is Downloads and <c>S:\Documents\Temp</c> is inside Documents. An
    /// ordinary folder in the profile reached the same way stays removable (§5.6).
    /// </summary>
    [Fact]
    public void RefusesTheAccountsFoldersReachedThroughASubstitutedLetter()
    {
        _volumes.Substituting(@"S:\", _environment.UserProfile);

        Assert.Equal("it is one of your own folders, where you keep your files.", WhyNotTaken(@"S:\Downloads"));
        Assert.Equal("it is one of your own folders, where you keep your files.", WhyNotTaken(@"\\?\S:\Downloads\"));
        Assert.Contains("your profile", WhyNotTaken(@"S:\AppData"), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_environment.UserProfile, "Documents"), PersonalFolderHolding(@"S:\Documents\Temp"));

        Assert.Null(WhyNotTaken(@"S:\Documents\Temp"));
        Assert.Null(WhyNotTaken(@"S:\shared-cache"));
        Assert.Null(PersonalFolderHolding(@"S:\shared-cache"));
        Assert.Null(PersonalFolderHolding(@"S:\AppData\Local\Temp"));
    }

    /// <summary>
    /// <c>subst</c> keeps the folder it was given in the form it was typed, so a letter made for the
    /// 8.3 alias of a folder holding the profile leads to a path that names the profile only in its
    /// short form. The place is compared without the alias, as the path asked about is.
    /// </summary>
    [Fact]
    public void RefusesTheAccountsFoldersReachedThroughALetterSubstitutedForAShortName()
    {
        _volumes.Substituting(@"S:\", ShortPath.Of(_temp.Path) ?? _temp.Path);

        Assert.Equal("it is one of your own folders, where you keep your files.", WhyNotTaken(@"S:\profile\Downloads"));
        Assert.Null(WhyNotTaken(@"S:\profile\shared-cache"));
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
