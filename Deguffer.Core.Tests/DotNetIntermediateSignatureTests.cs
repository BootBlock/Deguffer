using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rule that decides whether a directory named <c>obj</c> may be deleted.
///
/// Of 580 such directories surveyed across one source root, 238 were not SDK-style intermediate
/// output — and one held third-party art assets no build can regenerate. Every test below is a
/// shape that a name check, or a weaker evidence check, would get wrong.
/// </summary>
public sealed class DotNetIntermediateSignatureTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Project(string name, params string[] segments) =>
        _temp.CreateDirectory([.. segments, name]);

    /// <summary>
    /// Both schema versions seen in the wild. 3 is the common one; 4 is written by newer SDKs and
    /// accounted for 19 of the 342 recognised directories surveyed, so accepting only 3 would stop
    /// recognising real output as toolchains update.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void RecognisesAConventionalProjectAtEitherKnownSchemaVersion(int version)
    {
        var obj = ProjectFixture.CreateProject(Project("Example"), "Example", schemaVersion: version);

        var recognised = DotNetIntermediateSignature.TryRecognise(obj);

        Assert.NotNull(recognised);
        Assert.Equal("Example.csproj", recognised.ProjectName);
        Assert.True(LongPath.FileExists(recognised.ProjectFilePath));
    }

    /// <summary>
    /// An unknown schema is unrecognised rather than assumed compatible: a version this rule has
    /// not been checked against is a version whose fields may not mean what it assumes.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public void LeavesAManifestOfAnUnknownSchemaVersionAlone(int version)
    {
        var obj = ProjectFixture.CreateProject(Project("Example"), "Example", schemaVersion: version);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// §5.2's unrecognised case in its purest form, using the real shape: an asset pack's models,
    /// materials and importer sidecars. There is no manifest, so no amount of name matching should
    /// reach it. Deleting this loses files no build can reproduce.
    /// </summary>
    [Fact]
    public void LeavesADirectoryOfArtAssetsAloneBecauseItHasNoManifest()
    {
        var obj = ProjectFixture.CreateArtAssets(_temp.CreateDirectory("addons", "asset-pack", "Assets"));

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));

        // The models are still there to be found — the rule declined, it did not merely fail to run.
        Assert.True(LongPath.FileExists(Path.Combine(obj, "barrel.obj")));
    }

    /// <summary>
    /// Partial markers. Each of these is a directory that looks broadly right and cannot be
    /// confirmed, and every one of them must land Tier 4.
    /// </summary>
    [Fact]
    public void LeavesAManifestWithoutItsGeneratedPropsAlone()
    {
        var obj = ProjectFixture.CreateProject(Project("Example"), "Example", writeProps: false);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    [Fact]
    public void LeavesAManifestWithoutItsGeneratedTargetsAlone()
    {
        var obj = ProjectFixture.CreateProject(Project("Example"), "Example", writeTargets: false);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// The mismatched stem: imports are present, but named after a different project. Matching
    /// <c>*.nuget.g.props</c> would accept this; requiring the same stem as the manifest names does
    /// not, which is the difference between "looks like an obj" and "this project's obj".
    /// </summary>
    [Fact]
    public void LeavesGeneratedImportsNamedAfterADifferentProjectAlone()
    {
        var obj = ProjectFixture.CreateProject(
            Project("Example"), "Example", importStem: "Other.csproj");

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
        Assert.True(LongPath.FileExists(Path.Combine(obj, "Other.csproj.nuget.g.props")));
    }

    /// <summary>A manifest naming a project file that no longer exists anywhere.</summary>
    [Fact]
    public void LeavesADanglingManifestAlone()
    {
        var obj = ProjectFixture.CreateProject(
            Project("Example"), "Example", writeProjectFile: false);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// The case found by surveying real directories, and the reason the project file must be in the
    /// parent rather than merely existing somewhere. A source tree copied for backup keeps a
    /// manifest naming the original project, which is still on disk — so a rule that only checked
    /// the named path exists would let a manifest vouch for a directory it has nothing to do with.
    /// </summary>
    [Fact]
    public void LeavesAnObjAloneWhenItsManifestDescribesAProjectInAnotherDirectory()
    {
        var original = ProjectFixture.CreateProject(Project("Example", "original"), "Example");
        var elsewhere = Path.Combine(Path.GetDirectoryName(original)!, "Example.csproj");

        // The copy's own parent holds no project file; the manifest points at the original's.
        var copy = ProjectFixture.CreateProject(
            Project("Example", "backup"), "Example",
            manifestProjectPath: elsewhere,
            writeProjectFile: false);

        Assert.True(LongPath.FileExists(elsewhere), "The manifest's target must exist for this to be the real case.");
        Assert.Null(DotNetIntermediateSignature.TryRecognise(copy));
    }

    /// <summary>Truncated and nonsense manifests. Neither may throw; both are Tier 4.</summary>
    [Theory]
    [InlineData("{\"version\": 3, \"project\": {\"restore\": {\"projec")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"version\": \"3\"}")]
    [InlineData("[1, 2, 3]")]
    public void LeavesAMalformedManifestAloneWithoutThrowing(string manifest)
    {
        var obj = ProjectFixture.CreateProject(Project("Example"), "Example", rawManifest: manifest);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// A manifest naming something that is not a project file. The extension is the only thing
    /// distinguishing a project from any other path a hand-edited manifest could name.
    /// </summary>
    [Theory]
    [InlineData("Example.txt")]
    [InlineData("Example.sln")]
    [InlineData("Example")]
    public void LeavesAManifestNamingSomethingThatIsNotAProjectFileAlone(string named)
    {
        var directory = Project("Example");
        var obj = ProjectFixture.CreateProject(
            directory, "Example", manifestProjectPath: Path.Combine(directory, named));

        // The named file exists, so what disqualifies it is its extension and nothing else.
        File.WriteAllText(Path.Combine(directory, named), string.Empty);

        Assert.Null(DotNetIntermediateSignature.TryRecognise(obj));
    }

    /// <summary>
    /// A directory named <c>obj</c> holding nothing at all — the empty case, which carries no
    /// evidence either way. "No evidence" is not "recognised".
    /// </summary>
    [Fact]
    public void LeavesAnEmptyDirectoryAlone()
    {
        Assert.Null(DotNetIntermediateSignature.TryRecognise(_temp.CreateDirectory("Example", "obj")));
    }

    /// <summary>
    /// §6.3. A project nested past MAX_PATH must be recognised rather than silently skipped —
    /// truncation here would mean the directory is never offered, and the same truncation on the
    /// deletion side is a partial delete.
    /// </summary>
    [Fact]
    public void RecognisesAProjectNestedPastMaxPath()
    {
        var deep = _temp.Path;
        while (deep.Length < 240)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var directory = Path.Combine(deep, "Example");
        var obj = ProjectFixture.CreateProject(directory, "Example");

        Assert.True(obj.Length > 260, $"Path was only {obj.Length} characters.");

        var recognised = DotNetIntermediateSignature.TryRecognise(obj);

        Assert.NotNull(recognised);
        Assert.Equal("Example.csproj", recognised.ProjectName);
    }
}
