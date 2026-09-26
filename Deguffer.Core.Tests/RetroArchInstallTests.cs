using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder setting expanded as RetroArch's <c>fill_pathname_expand_special</c> expands it. Every
/// Windows default is relative to the program, so this is where a folder is decided.
/// </summary>
public sealed class RetroArchInstallTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RetroArchInstallTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Program => Path.Combine(_temp.Path, "RetroArch");

    private RetroArchInstall Install(string? program, params string[] lines)
    {
        var file = Path.Combine(_temp.Path, "settings", "retroarch.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        return new RetroArchInstall(program, RetroArchSettings.Read(file));
    }

    private RetroArchFolder Thumbnails(RetroArchInstall install) =>
        install.Folder(RetroArchFolderSetting.Thumbnails, _environment);

    [Fact]
    public void WithNoSettingsEveryFolderIsBesideTheProgram()
    {
        var install = new RetroArchInstall(Program, null);

        Assert.Equal(Path.Combine(Program, "thumbnails"), Thumbnails(install).Path);
        Assert.Equal(Path.Combine(Program, "shaders"), install.Folder(RetroArchFolderSetting.Shaders, _environment).Path);
        Assert.Equal(Path.Combine(Program, "database", "rdb"), install.Folder(RetroArchFolderSetting.Database, _environment).Path);
    }

    /// <summary>RetroArch skips the character after the colon whatever it is.</summary>
    [Theory]
    [InlineData(@":\art")]
    [InlineData(":/art")]
    public void AColonIsTheProgramsFolder(string value)
    {
        var install = Install(Program, $"thumbnails_directory = \"{value}\"");

        Assert.Equal(Path.Combine(Program, "art"), Thumbnails(install).Path);
    }

    /// <summary>
    /// <c>default</c> is no folder for the thumbnails and the shaders, and a literal relative path for
    /// the database, which can then not be placed.
    /// </summary>
    [Fact]
    public void TheWordDefaultIsNoFolderExceptForTheDatabase()
    {
        var install = Install(Program, "thumbnails_directory = \"default\"", "content_database_path = \"default\"");

        Assert.Equal(new RetroArchFolder(null, null), Thumbnails(install));

        var database = install.Folder(RetroArchFolderSetting.Database, _environment);
        Assert.Null(database.Path);
        Assert.NotNull(database.Unresolved);
    }

    [Fact]
    public void AnEmptyValueIsNoFolder()
    {
        var install = Install(Program, "thumbnails_directory = \"\"");

        Assert.Equal(new RetroArchFolder(null, null), Thumbnails(install));
    }

    [Fact]
    public void ATildeIsHomeWhereHomeIsSet()
    {
        var home = Path.Combine(_temp.Path, "home");
        _environment.WithEnvironmentVariable("HOME", home);

        Assert.Equal(Path.Combine(home, "art"), Thumbnails(Install(Program, "thumbnails_directory = \"~\\art\"")).Path);
    }

    [Fact]
    public void ATildeWithNoHomeCannotBePlaced()
    {
        var folder = Thumbnails(Install(Program, "thumbnails_directory = \"~\\art\""));

        Assert.Null(folder.Path);
        Assert.NotNull(folder.Unresolved);
    }

    [Fact]
    public void AFullPathIsTakenAsItIs()
    {
        var art = Path.Combine(_temp.Path, "art");

        Assert.Equal(art, Thumbnails(Install(Program, $"thumbnails_directory = \"{art}\"")).Path);
    }

    /// <summary>A relative path is relative to where RetroArch was started from, which nothing records.</summary>
    [Fact]
    public void ARelativePathCannotBePlaced()
    {
        var folder = Thumbnails(Install(Program, "thumbnails_directory = \"art\""));

        Assert.Null(folder.Path);
        Assert.NotNull(folder.Unresolved);
    }

    [Fact]
    public void AColonWithNoKnownProgramAsksForTheFolder()
    {
        var folder = Thumbnails(Install(null, "thumbnails_directory = \":\\thumbnails\""));

        Assert.Null(folder.Path);
        Assert.Contains("Emulator folders", folder.Unresolved, StringComparison.Ordinal);
    }
}
