using Deguffer.Core.Duplicates;
using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.4's default exclusions: what Explore refuses at and below, and program folders, are passed
/// over, each is named with its reason, and an install location that names the user's own files is
/// set aside rather than obeyed.
///
/// <para>Every file below has one length, so any two that are searched make a group: what is passed
/// over is exactly what is missing from it.</para>
/// </summary>
public sealed class DuplicatePassedOverTests : IDisposable
{
    private const int Length = 64;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Profile => _tree.Environment.UserProfile;

    private IReadOnlyList<string> Plant()
    {
        _tree.File(Length, "Windows", "System32", "a.dll");
        _tree.File(Length, "Program Files", "Vendor", "b.dll");
        _tree.File(Length, "ProgramData", "Vendor", "c.dat");
        _tree.File(Length, "Users", "other", "Documents", "d.txt");
        _tree.File(Length, "$Recycle.Bin", "S-1-5-21-1", "e.txt");
        _tree.File(Length, "System Volume Information", "f.dat");
        _tree.File(Length, "Apps", "Tool", "g.dll");
        _tree.File(Length, "Users", "profile", "AppData", "Local", "Programs", "Editor", "h.dll");
        _tree.File(Length, "Users", "profile", "AppData", "Local", "Microsoft", "Outlook", "i.ost");
        _tree.File(Length, "Data", "Outlook Files", "j.pst");
        _tree.File(Length, "Users", "profile", "Documents", "Mail", "archive.pst");

        _tree.Registry.With(
            UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(_tree.Top, "Apps", "Tool")));

        return
        [
            _tree.File(Length, "Users", "profile", "Documents", "k.txt"),
            _tree.File(Length, "Users", "profile", "Downloads", "l.txt"),
            _tree.File(Length, "Data", "m.txt"),
        ];
    }

    [Fact]
    public async Task EveryDefaultExclusionIsPassedOverAndNamed()
    {
        var searched = Plant();

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Top));

        Assert.Equal(searched.Order(), Assert.Single(found.Groups).Files.Select(file => file.Path).Order());

        var passedOver = found.PassedOver.Select(place => place.Path).ToList();

        Assert.Contains(_tree.System.WindowsDirectory, passedOver);
        Assert.Contains(_tree.System.ProgramFiles, passedOver);
        Assert.Contains(_tree.System.ProgramData, passedOver);
        Assert.Contains(Path.Combine(_tree.Users, "other"), passedOver);
        Assert.Contains(Path.Combine(_tree.Top, "$Recycle.Bin"), passedOver);
        Assert.Contains(Path.Combine(_tree.Top, "System Volume Information"), passedOver);
        Assert.Contains(Path.Combine(_tree.Top, "Apps", "Tool"), passedOver);
        Assert.Contains(Path.Combine(_tree.Environment.LocalAppData, "Programs"), passedOver);
        Assert.Contains(Path.Combine(_tree.Environment.LocalAppData, "Microsoft", "Outlook"), passedOver);
        Assert.Contains(Path.Combine(_tree.Top, "Data", "Outlook Files"), passedOver);
        Assert.Contains(Path.Combine(Profile, "Documents", "Mail", "archive.pst"), passedOver);
    }

    /// <summary>
    /// Explore's refusals are asked of Explore's policy, so each reason is the policy's own, and the
    /// two cannot come to disagree about what Explore refuses.
    /// </summary>
    [Fact]
    public async Task EveryExclusionExploreMakesIsTheReasonExploreGives()
    {
        Plant();
        var policy = _tree.Policy();

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Top));

        foreach (var place in found.PassedOver.Where(place => !place.Reason.Contains("is installed here", StringComparison.Ordinal)))
        {
            Assert.Equal(policy.RefusedAtAndBelow(place.Path)?.Reason, place.Reason);
        }

        Assert.Contains(found.PassedOver, place => place.Reason.Contains("'Tool' is installed here", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Users folder is refused by Explore and holds the signed-in profile, which is not, so it is
    /// not passed over whole: the profile is searched, and the other accounts beside it are not.
    /// </summary>
    [Fact]
    public async Task TheUsersFolderAndTheSignedInProfileAreSearched()
    {
        Plant();

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Users));

        Assert.Empty(found.Unsearched);
        Assert.DoesNotContain(found.PassedOver, place => place.Path == _tree.Users || place.Path == Profile);
        Assert.Contains(found.PassedOver, place => place.Path == Path.Combine(_tree.Users, "other"));
        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>
    /// What Windows keeps at the top of a volume is passed over on every volume, a data drive that
    /// holds none of Windows' own folders included.
    /// </summary>
    [Fact]
    public async Task WhatWindowsKeepsAtTheTopOfADataDriveIsPassedOver()
    {
        var drive = _tree.Folder("DataDrive") + Path.DirectorySeparatorChar;
        _tree.Volumes.With(drive);
        _tree.File(Length, "DataDrive", "$Recycle.Bin", "S-1-5-21-1", "a.txt");
        _tree.File(Length, "DataDrive", "System Volume Information", "b.dat");
        _tree.File(Length, "DataDrive", "Photos", "c.jpg");
        _tree.File(Length, "DataDrive", "Backup", "c.jpg");

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(drive));

        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
        Assert.Equal(
            ["$Recycle.Bin", "System Volume Information"],
            found.PassedOver.Select(place => Path.GetFileName(place.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALocationThatIsItselfExcludedIsPassedOverAndNamed()
    {
        Plant();
        _tree.File(Length, "Windows", "System32", "copy.dll");

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.System.WindowsDirectory));

        Assert.Empty(found.Groups);
        Assert.Equal(_tree.System.WindowsDirectory, Assert.Single(found.PassedOver).Path);
    }

    [Fact]
    public async Task ASearchAskedToIncludeThemSearchesThePlacesItWouldPassOver()
    {
        Plant();

        var found = await _tree.FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new(_tree.Top)], searchPassedOverPlaces: true));

        Assert.Empty(found.PassedOver);
        Assert.Equal(14, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>
    /// The policy is asked about the folders where its answer can change, not about each folder. This
    /// asks it about every file and folder on the drive, and checks that what the search passed over is
    /// exactly the outermost of what it refuses.
    /// </summary>
    [Fact]
    public async Task WhatIsPassedOverIsWhatThePolicyRefusesAskedOfEveryFolder()
    {
        Plant();
        _tree.File(Length, "pagefile.sys");
        _tree.File(Length, "Users", "profile", "AppData", "Roaming", "Tool", "n.txt");
        _tree.File(Length, "Users", "Public", "o.txt");
        var policy = _tree.Policy();

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Top));

        var everything = Directory.EnumerateFileSystemEntries(_tree.Top, "*", SearchOption.AllDirectories);

        string[] programFolders = [Path.Combine(_tree.Top, "Apps", "Tool"), Path.Combine(_tree.Environment.LocalAppData, "Programs")];

        var outermost = everything
            .Where(path => policy.RefusedAtAndBelow(path) is not null
                && policy.RefusedAtAndBelow(Path.GetDirectoryName(path)!) is null)
            .Concat(programFolders)
            .Order()
            .ToList();

        Assert.True(outermost.Count > programFolders.Length);
        Assert.Equal(
            outermost.Select(path => Path.GetRelativePath(_tree.Top, path)),
            found.PassedOver.Select(place => Path.GetRelativePath(_tree.Top, place.Path)).Order());
    }

    [Fact]
    public async Task AnInstallLocationNamingTheUsersOwnFilesIsSetAsideAndNamed()
    {
        var documents = _tree.Environment.Documents!;
        _tree.File(Length, "Users", "profile", "Documents", "a.txt");
        _tree.File(Length, "Users", "profile", "Desktop", "b.txt");

        _tree.Registry.With(UninstallScope.Machine64, "Drive", ("DisplayName", "Whole drive"), ("InstallLocation", _tree.Top));
        _tree.Registry.With(UninstallScope.Machine32, "Profile", ("DisplayName", "Whole profile"), ("InstallLocation", Profile));
        _tree.Registry.With(UninstallScope.CurrentUser, "Docs", ("DisplayName", "Documents"), ("InstallLocation", documents));
        _tree.Registry.With(
            UninstallScope.CurrentUser, "Other", ("DisplayName", "Other account"), ("InstallLocation", Path.Combine(_tree.Users, "other")));

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(Profile));

        Assert.Equal(
            ["Documents", "Other account", "Whole drive", "Whole profile"],
            found.SetAside.Select(entry => entry.Program).Order());
        Assert.DoesNotContain(found.PassedOver, place => place.Path == documents || place.Path == Profile);
        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>An install location holding a folder the user chose would silence that choice.</summary>
    [Fact]
    public async Task AnInstallLocationHoldingAChosenLocationIsSetAside()
    {
        _tree.File(Length, "Games", "Saves", "a.sav");
        _tree.File(Length, "Games", "Saves", "Old", "a.sav");
        _tree.Registry.With(
            UninstallScope.Machine64, "Launcher", ("DisplayName", "Launcher"), ("InstallLocation", Path.Combine(_tree.Top, "Games")));

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(Path.Combine(_tree.Top, "Games", "Saves")));

        Assert.Equal("Launcher", Assert.Single(found.SetAside).Program);
        Assert.Empty(found.PassedOver);
        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>
    /// A search walks final paths, so a program installed at a path with a junction on the way to it
    /// is passed over at the folder the junction leads to.
    /// </summary>
    [Fact]
    public async Task AProgramFolderNamedThroughAJunctionIsPassedOverWhereItLeads()
    {
        var real = _tree.Folder("Real", "Apps");
        var apps = Path.Combine(_tree.Top, "Apps");
        Junction.ToDirectory(apps, real);
        var installed = _tree.File(Length, "Real", "Apps", "Tool", "a.dll");
        _tree.File(Length, "Real", "Data", "b.dll");
        _tree.File(Length, "Real", "Data", "c.dll");
        _tree.Registry.With(
            UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(apps, "Tool")));

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(Path.Combine(_tree.Top, "Real")));

        var tool = Assert.Single(found.PassedOver, place => place.Path == Path.GetDirectoryName(installed));
        Assert.Contains("'Tool' is installed here", tool.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(installed, Assert.Single(found.Groups).Files.Select(file => file.Path));
    }

    [Fact]
    public async Task AListOfInstalledProgramsWindowsWouldNotReadIsReported()
    {
        _tree.Registry.Refusing(UninstallScope.Machine64);

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(Profile));

        Assert.Equal(UninstallScope.Machine64, Assert.Single(found.UnreadProgramLists));
    }
}
