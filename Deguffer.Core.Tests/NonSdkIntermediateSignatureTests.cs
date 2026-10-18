using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rule that decides whether an old-style (non-SDK) project's <c>obj</c> may be deleted. It has
/// no restore manifest to read, so every test below is a shape where weaker evidence than a file
/// list naming its own directory, agreeing bookkeeping and an old-style project beside it would
/// reach something that is not that project's output.
/// </summary>
public sealed class NonSdkIntermediateSignatureTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Folder(params string[] segments) => _temp.CreateDirectory(segments);

    [Theory]
    [InlineData(".vbproj")]
    [InlineData(".csproj")]
    [InlineData(".fsproj")]
    public void RecognisesABuiltOldStyleProjectInEveryLanguage(string extension)
    {
        var directory = Folder("Example");
        var obj = ProjectFixture.CreateLegacyProject(directory, "Example", extension);

        var recognised = NonSdkIntermediateSignature.TryRecognise(obj);

        Assert.NotNull(recognised);
        Assert.Equal("Example" + extension, recognised.ProjectName);
        Assert.Equal(Path.Combine(directory, "Example" + extension), recognised.ProjectFilePath);
    }

    /// <summary>
    /// The layout the issue was reported from: platform directories holding configurations, and
    /// configurations only a design-time build touched, which carry no file list at all.
    /// </summary>
    [Fact]
    public void RecognisesPlatformDirectoriesAndConfigurationsOnlyADesignTimeBuildTouched()
    {
        var obj = ProjectFixture.CreateLegacyProject(
            Folder("Example"),
            "Example",
            built: ["Debug", Path.Combine("x86", "Debug")],
            designTime: ["Release", Path.Combine("x64", "Debug"), Path.Combine("x64", "Release")]);

        Assert.Equal("Example.vbproj", NonSdkIntermediateSignature.TryRecognise(obj)?.ProjectName);
    }

    /// <summary>
    /// A design-time build writes the same stubs into every configuration, and the names are parsed
    /// rather than matched loosely: a GUID-named stub is accepted, a stub not named by a GUID is not.
    /// </summary>
    [Theory]
    [InlineData("TemporaryGeneratedFile_036C0B5B-1481-4323-8D20-8F5ADCB23D92.cs", true)]
    [InlineData("TemporaryGeneratedFile_notes.cs", false)]
    [InlineData("Example.vbprojAssemblyReference.cache", true)]
    [InlineData("Example.vbproj.CoreCompileInputs.cache", true)]
    [InlineData("notes.txt", false)]
    public void AcceptsOnlyWhatADesignTimeBuildWritesInAConfigurationWithNoFileList(string name, bool recognised)
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example", designTime: ["Release"]);
        File.WriteAllText(Path.Combine(obj, "Release", name), string.Empty);

        Assert.Equal(recognised, NonSdkIntermediateSignature.TryRecognise(obj) is not null);
    }

    /// <summary>
    /// <c>TempPE</c> is accepted empty in a design-time configuration, because that is all a
    /// design-time build was seen to leave there. Anything inside it is unexplained.
    /// </summary>
    [Fact]
    public void LeavesADesignTimeConfigurationWithSomethingInItsTempPEAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example", designTime: ["Release"]);
        File.WriteAllText(Path.Combine(obj, "Release", "TempPE", "Form1.Designer.vb.dll"), string.Empty);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// Design-time configurations alone are not enough: nothing in them names a path, so nothing
    /// ties them to the directory in hand. The project-named cache in one is there so that the
    /// project is named, and what is missing is the file list and nothing else.
    /// </summary>
    [Fact]
    public void LeavesAnObjNoBuildRanInAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example", built: [], designTime: ["Debug"]);
        File.WriteAllText(Path.Combine(obj, "Debug", "Example.vbprojAssemblyReference.cache"), "0");

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    [Fact]
    public void LeavesAnObjWithNoProjectFileInItsParentAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example", writeProjectFile: false);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// An SDK-style project beside the bookkeeping is not this rule's to recognise, including one
    /// that keeps the 2003 namespace, which the SDK allows. That is
    /// <see cref="DotNetIntermediateSignature"/>'s, and it has its own evidence.
    /// </summary>
    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />")]
    [InlineData("<Project ToolsVersion=\"15.0\" />")]
    [InlineData("<Root xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />")]
    [InlineData("not xml at all")]
    [InlineData("<!DOCTYPE Project [<!ENTITY x \"y\">]><Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />")]
    public void LeavesAnObjBesideAnythingButAnOldStyleProjectAlone(string projectXml)
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example", projectXml: projectXml);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// Bookkeeping named after a different project. The parent holds both project files, so what
    /// disqualifies it is the stem and nothing else.
    /// </summary>
    [Fact]
    public void LeavesBookkeepingForADifferentProjectAlone()
    {
        var directory = Folder("Example");
        var obj = ProjectFixture.CreateLegacyProject(directory, "Example", bookkeepingStem: "Other.vbproj");
        File.WriteAllText(Path.Combine(directory, "Other.vbproj"), ProjectFixture.LegacyProjectXml);
        File.Delete(Path.Combine(directory, "Example.vbproj"));

        Assert.Equal("Other.vbproj", NonSdkIntermediateSignature.TryRecognise(obj)?.ProjectName);

        File.Delete(Path.Combine(directory, "Other.vbproj"));
        File.WriteAllText(Path.Combine(directory, "Example.vbproj"), ProjectFixture.LegacyProjectXml);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// Two projects' records in one <c>obj</c>: a directory shared by two builds, or one project's
    /// files dropped among another's. Either way the directory is not one project's output.
    /// </summary>
    [Fact]
    public void LeavesAConfigurationHoldingTwoProjectsBookkeepingAlone()
    {
        var directory = Folder("Example");
        var obj = ProjectFixture.CreateLegacyProject(directory, "Example");
        File.WriteAllText(Path.Combine(directory, "Other.vbproj"), ProjectFixture.LegacyProjectXml);
        File.WriteAllText(Path.Combine(obj, "Debug", "Other.vbproj.CoreCompileInputs.cache"), "0");

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// Anything at the <c>obj</c> root but a directory. No old-style build surveyed wrote a file
    /// there, so one is something this rule has not seen MSBuild write.
    /// </summary>
    [Fact]
    public void LeavesAnObjWithAFileAtItsRootAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example");
        File.WriteAllText(Path.Combine(obj, "barrel.obj"), "v 0.0 0.0 0.0");

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// A directory beside the configurations that is neither one nor a platform holding them: art
    /// assets kept in the same folder as a build that once ran there.
    /// </summary>
    [Fact]
    public void LeavesAnObjWithAnUnexplainedDirectoryAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example");
        ProjectFixture.CreateArtAssets(obj);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// A platform directory may hold configurations and nothing else. A configuration nested a
    /// level deeper than a platform is not a layout MSBuild writes.
    /// </summary>
    [Fact]
    public void LeavesAConfigurationNestedPastAPlatformAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(
            Folder("Example"), "Example", built: ["Debug", Path.Combine("x64", "nested", "Debug")]);

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    [Fact]
    public void LeavesAnEmptyObjAlone()
    {
        Assert.Null(NonSdkIntermediateSignature.TryRecognise(Folder("Example", "obj")));
    }

    /// <summary>
    /// The copied-tree hole, closed without a manifest. The copy's file lists name the original's
    /// directories, which exist, so a rule that resolved them would let them vouch for the copy.
    /// Comparing them with the directory in hand does not.
    /// </summary>
    [Fact]
    public void LeavesAnObjAloneWhenItsFileListsNameAnotherDirectory()
    {
        var original = Folder("original", "Example");
        var originalObj = ProjectFixture.CreateLegacyProject(original, "Example");
        var copy = ProjectFixture.CreateLegacyProject(Folder("backup", "Example"), "Example", fileListRoot: original);

        Assert.NotNull(NonSdkIntermediateSignature.TryRecognise(originalObj));
        Assert.Null(NonSdkIntermediateSignature.TryRecognise(copy));
    }

    /// <summary>
    /// A file list must be what MSBuild writes, absolute paths only. One relative line means it is
    /// not, whatever else it names.
    /// </summary>
    [Fact]
    public void LeavesAFileListWithARelativePathAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example");
        File.AppendAllText(Path.Combine(obj, "Debug", "Example.vbproj.FileListAbsolute.txt"), @"obj\Debug\Example.exe");

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// File lists for two projects in one configuration. Each list is named after its project, so
    /// two of them are two projects, and the bookkeeping cannot agree.
    /// </summary>
    [Fact]
    public void LeavesAConfigurationWithFileListsForTwoProjectsAlone()
    {
        var obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example");
        File.Copy(
            Path.Combine(obj, "Debug", "Example.vbproj.FileListAbsolute.txt"),
            Path.Combine(obj, "Debug", "Example.csproj.FileListAbsolute.txt"));

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// A link at either level is a tree nobody examined: the <c>obj</c> itself, its project
    /// directory, a configuration and a platform's configuration.
    ///
    /// <para>Each link is the only thing wrong. A link to the <c>obj</c> or the project leads to file
    /// lists written by a build that ran through that link, so they name the path in hand. A link
    /// to a configuration leads to one only a design-time build touched, which names no path at
    /// all. Without the link checks, every one of these is recognised.</para>
    /// </summary>
    [Theory]
    [InlineData("obj")]
    [InlineData("project")]
    [InlineData("configuration")]
    [InlineData("platform configuration")]
    public void LeavesAnObjWithALinkAtEitherLevelAlone(string where)
    {
        var elsewhere = Folder("elsewhere");
        string obj;

        switch (where)
        {
            case "obj":
                var directory = Folder("Example");
                File.WriteAllText(Path.Combine(directory, "Example.vbproj"), ProjectFixture.LegacyProjectXml);
                obj = Path.Combine(directory, "obj");
                SymbolicLink.ToDirectory(
                    obj,
                    ProjectFixture.CreateLegacyProject(Path.Combine(elsewhere, "Example"), "Example", fileListRoot: directory));
                break;

            case "project":
                var link = Path.Combine(_temp.Path, "Linked");
                var target = Path.Combine(elsewhere, "Example");
                ProjectFixture.CreateLegacyProject(target, "Example", fileListRoot: link);
                SymbolicLink.ToDirectory(link, target);
                obj = Path.Combine(link, "obj");
                break;

            case "configuration":
                var designed = ProjectFixture.CreateLegacyProject(
                    Path.Combine(elsewhere, "Example"), "Example", designTime: ["Release"]);
                obj = ProjectFixture.CreateLegacyProject(Folder("Example"), "Example");
                SymbolicLink.ToDirectory(Path.Combine(obj, "Release"), Path.Combine(designed, "Release"));
                break;

            default:
                var platform = ProjectFixture.CreateLegacyProject(
                    Path.Combine(elsewhere, "Example"), "Example", designTime: [Path.Combine("x64", "Release")]);
                obj = ProjectFixture.CreateLegacyProject(
                    Folder("Example"), "Example", built: ["Debug", Path.Combine("x64", "Debug")]);
                SymbolicLink.ToDirectory(Path.Combine(obj, "x64", "Release"), Path.Combine(platform, "x64", "Release"));
                break;
        }

        Assert.Null(NonSdkIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// §6.3, by the form of the path. Windows drops a trailing dot from every name in a path it
    /// parses, and only the <c>\\?\</c> form keeps it, so a project directory and a configuration
    /// whose names end in one can be read only through <see cref="LongPath.Extended"/>. Without it,
    /// the project file and the file list are looked for in <c>Example</c> and <c>Debug</c>, which
    /// are not there, and the directory goes unrecognised. A tree past <c>MAX_PATH</c> could not show
    /// this, because .NET adds the prefix itself at that length.
    /// </summary>
    [Fact]
    public void ReadsTheProjectAndItsFileListsThroughTheExtendedForm()
    {
        var directory = Path.Combine(_temp.Path, "Example.");
        var obj = ProjectFixture.CreateLegacyProject(directory, "Example", built: ["Debug."]);

        Assert.False(File.Exists(Path.Combine(directory, "Example.vbproj")), "Windows must reread the name for this to test the form.");

        Assert.Equal(
            Path.Combine(directory, "Example.vbproj"),
            NonSdkIntermediateSignature.TryRecognise(obj)?.ProjectFilePath);
    }
}
