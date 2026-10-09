using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>§7.4's program folders: a copy in a folder an installed program's entry names is refused, wherever the entry's path leads.</summary>
public sealed class DuplicateProgramFolderRefusalTests : DuplicateMarkingScene
{
    /// <summary>
    /// A search's paths are final paths, so a program installed at a path with a junction on the way
    /// is at the folder the junction leads to, and a copy found there is refused.
    /// </summary>
    [Fact]
    public void ACopyIsRefusedInAProgramsFolderReachedThroughAJunction()
    {
        var real = _tree.Folder("Real", "Apps");
        var apps = Path.Combine(_tree.Top, "Apps");
        Junction.ToDirectory(apps, real);
        var tool = _tree.Folder("Real", "Apps", "Tool");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(apps, "Tool")));
        _programs.AddRange(ReadProgramFolders().Installed);

        var installed = Copy(Path.Combine(tool, "a.dll"));
        var marks = Marks([installed, Copy(Path.Combine(Downloads, "a.dll"))]);

        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(installed));
    }

    /// <summary>
    /// Two entries naming one folder, one through a junction on the way and one where it leads, are
    /// one program folder, however each is spelled.
    /// </summary>
    [Fact]
    public void TwoEntriesThatReachOneFolderAreOneProgramFolder()
    {
        var real = _tree.Folder("Real", "Apps");
        var apps = Path.Combine(_tree.Top, "Apps");
        Junction.ToDirectory(apps, real);
        _tree.Folder("Real", "Apps", "Tool");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(real, "Tool")));
        _tree.Registry.With(InstalledApps.UninstallScope.Machine32, "Tool32", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(apps, "Tool")));

        var reading = ReadProgramFolders();

        Assert.Single(reading.Installed, folder => folder.Program == "Tool");
        Assert.Single(reading.Folders, folder => folder.Program == "Tool");
    }

    /// <summary>
    /// An install location named through a junction that leads to a folder holding a chosen location
    /// would silence that choice, so it is set aside, and is still a folder a copy is refused in.
    /// </summary>
    [Fact]
    public void AnInstallLocationThatLeadsToAFolderHoldingAChosenLocationIsSetAside()
    {
        var real = _tree.Folder("Real", "Apps");
        var apps = Path.Combine(_tree.Top, "Apps");
        Junction.ToDirectory(apps, real);
        var chosen = _tree.Folder("Real", "Apps", "Tool", "Assets");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(apps, "Tool")));

        var reading = ReadProgramFolders(chosen);

        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.DoesNotContain(reading.Folders, folder => folder.Program == "Tool");
        Assert.Contains(reading.Installed, folder => folder.Program == "Tool");
    }

    /// <summary>
    /// An install location is judged where it leads as well as as it is named: one that is a junction
    /// to the profile has named the profile, which is neither passed over nor refused in.
    /// </summary>
    [Fact]
    public void AnInstallLocationThatLeadsToTheProfileIsSetAside()
    {
        Directory.CreateDirectory(_tree.Environment.UserProfile);
        var named = Path.Combine(_tree.Folder("Apps"), "Tool");
        Junction.ToDirectory(named, _tree.Environment.UserProfile);
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", named));

        var reading = ReadProgramFolders();

        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.DoesNotContain(reading.Installed, folder => folder.Program == "Tool");
    }

    /// <summary>
    /// An install location on a share is never opened to follow it to its final path, as a search
    /// location on one is not: opening it is itself a conversation with another machine.
    /// </summary>
    [Fact]
    public void AnInstallLocationOnAShareIsNeverOpened()
    {
        List<string> opened = [];
        var files = new FileInformation(
            (path, use) =>
            {
                opened.Add(path);
                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity);
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Shared", ("DisplayName", "Shared"), ("InstallLocation", @"\\server.test\apps\Tool"));
        _tree.Registry.With(InstalledApps.UninstallScope.Machine32, "Local", ("DisplayName", "Local"), ("InstallLocation", _tree.Folder("Apps", "Local")));

        ReadProgramFolders(files);

        Assert.Contains(opened, path => path.EndsWith(@"\Apps\Local", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(opened, path => path.Contains("server.test", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A copy in a program's folder that a search went into is refused: one searched because the
    /// search includes the places it passes over by default, and one set aside because it holds a
    /// location the search was asked to search.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyInAProgramsFolderTheSearchWentIntoIsRefused(bool setAside)
    {
        var tool = _tree.Folder("Apps", "Tool");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", tool));
        var installed = _tree.File(100, "Apps", "Tool", "Assets", "a.dll");
        _tree.File(100, "Data", "a.dll");
        var data = new SearchLocation(Path.Combine(_tree.Top, "Data"));
        SearchLocation[] locations = setAside
            ? [new SearchLocation(Path.Combine(tool, "Assets")), data]
            : [new SearchLocation(Path.Combine(_tree.Top, "Apps")), data];

        var result = await _tree.Searcher().SearchAsync(
            new DuplicateSearch(MatchCriteria.Size, locations, searchPassedOverPlaces: !setAside), _tree.Policy());
        var marks = MarksOf(result);

        Assert.Equal(setAside, result.Finding.SetAside.Any(entry => entry.Program == "Tool"));
        var copy = Assert.Single(Only(marks).Group.Files, file => file.Path == installed);
        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(copy));
    }

    [Fact]
    public void AnOnlineOnlyCopyAndACopyInAProgramFolderAreRefused()
    {
        var tool = Path.Combine(_tree.Top, "Apps", "Tool");
        _programs.Add(new ProgramFolder(tool, ReachedFolder.At(tool, _tree.Volumes), "Tool", Final: null));

        var online = Copy(Path.Combine(Documents, "a.dll"), storage: FileStorage.CloudOnly);
        var installed = Copy(Path.Combine(tool, "a.dll"));
        var marks = Marks([online, installed, Copy(Path.Combine(Downloads, "a.dll"))]);
        var group = Only(marks);

        Assert.Contains("online-only", group.Mark(online, marks.Keeping));
        Assert.Contains("'Tool' is installed here", group.Mark(installed, marks.Keeping));
        Assert.NotNull(marks.Keeping.WhyNotKept(online));
        Assert.NotNull(marks.Keeping.WhyNotKept(installed));
    }

    [Fact]
    public void AProgramsInstallLocationThatHoldsAChosenLocationIsStillAFolderACopyIsRefusedIn()
    {
        var tool = _tree.Folder("Apps", "Tool");
        var chosen = _tree.Folder("Apps", "Tool", "Assets");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", tool));

        var reading = ReadProgramFolders(chosen);

        Assert.DoesNotContain(reading.Folders, folder => folder.Program == "Tool");
        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.Contains(reading.Installed, folder => folder.Program == "Tool");
    }
}
