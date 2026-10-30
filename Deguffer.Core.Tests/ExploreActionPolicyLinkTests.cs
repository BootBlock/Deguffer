using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1 through a link: Explore judges a removal where it really acts, not where the path's text
/// says. A junction on the way to an item puts the removal on the link's far side, and a protected
/// folder moved by a junction is reached at the far side without its name.
///
/// <para>Real junctions, because the question is what Windows follows. The fakes' Windows directory,
/// <c>ProgramData</c> and the Users folder are under one scratch folder, so a second one stands for
/// another drive, which no rule names.</para>
/// </summary>
public sealed class ExploreActionPolicyLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _elsewhere = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;

    public ExploreActionPolicyLinkTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose()
    {
        _temp.Dispose();
        _elsewhere.Dispose();
    }

    private string GradleRoot => Path.Combine(_environment.UserProfile, ".gradle");

    /// <summary>
    /// A junction in the profile, which the profile's permission covers, leading to the Windows
    /// directory, another account's profile or machine-wide data. What is below the junction is
    /// what those rules protect, and it is refused with a sentence that names the link.
    /// </summary>
    [Theory]
    [InlineData("Windows")]
    [InlineData("ProgramData")]
    [InlineData("another-account")]
    public void AnItemThroughALinkIsJudgedWhereTheLinkLeads(string protectedFolder)
    {
        var target = _temp.CreateDirectory(protectedFolder, "System32");
        var link = Path.Combine(_environment.UserProfile, "shortcut");
        Junction.ToDirectory(link, Path.GetDirectoryName(target)!);

        var verdict = Policy().MayRemove(Path.Combine(link, "System32"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("is a link", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(target, verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The link itself is judged as itself: removing a junction removes the junction and never what
    /// it leads to, so following it would refuse a removal that touches nothing protected.
    /// </summary>
    [Fact]
    public void TheLinkItselfIsJudgedByWhereItIs()
    {
        var link = Path.Combine(_environment.UserProfile, "shortcut");
        Junction.ToDirectory(link, _system.WindowsDirectory);

        Assert.True(Policy().MayRemove(link).IsAllowed);
    }

    /// <summary>
    /// A link to somewhere no rule names changes nothing. Explore can be scoped to a link on purpose,
    /// so following one must not refuse what is ordinary on its far side.
    /// </summary>
    [Fact]
    public void AnItemThroughALinkToAnOrdinaryFolderIsAllowed()
    {
        var target = _elsewhere.CreateDirectory("Photos", "2019");
        var link = Path.Combine(_environment.UserProfile, "Photos");
        Junction.ToDirectory(link, Path.GetDirectoryName(target)!);

        Assert.True(Policy().MayRemove(Path.Combine(link, "2019")).IsAllowed);
    }

    /// <summary>
    /// A link on the way into a tool's folder puts the item below the tool root, where §5.2's
    /// unrecognised case refuses it.
    /// </summary>
    [Fact]
    public void AnUnrecognisedChildOfAToolRootReachedThroughALinkIsRefused()
    {
        _temp.CreateDirectory("profile", ".gradle");
        var link = Path.Combine(_elsewhere.Path, "gradle-home");
        Junction.ToDirectory(link, GradleRoot);

        var policy = Policy(Gradle());

        Assert.False(policy.MayRemove(Path.Combine(link, "gradle.properties")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(link, "caches")).IsAllowed);
    }

    /// <summary>
    /// A tool's folder moved by a junction, scanned at the far side. The root, its unrecognised
    /// children and the folder holding it are refused there as they are at the declared path, and
    /// what the provider recognises is not. This is §5.2's unrecognised case at the far side.
    /// </summary>
    [Fact]
    public void AToolRootIsRecognisedAtTheFarSideOfALink()
    {
        var moved = _elsewhere.CreateDirectory("gradle");
        Junction.ToDirectory(GradleRoot, moved);

        var policy = Policy(Gradle());
        var properties = policy.MayRemove(Path.Combine(moved, "gradle.properties"));

        Assert.False(properties.IsAllowed);
        Assert.Contains("not something Deguffer recognises", properties.Reason, StringComparison.Ordinal);
        Assert.False(policy.MayRemove(Path.Combine(moved, "init.d", "company.gradle")).IsAllowed);
        Assert.False(policy.MayRemove(moved).IsAllowed);
        Assert.False(policy.MayRemove(_elsewhere.Path).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(moved, "caches", "modules-2")).IsAllowed);
    }

    /// <summary>
    /// A refusing region moved by a junction is refused at the far side, by both questions the
    /// policy answers, since a search passes over what <see cref="ExploreActionPolicy.RefusedAtAndBelow"/>
    /// refuses.
    /// </summary>
    [Fact]
    public void ARefusingRegionIsRefusedAtTheFarSideOfALink()
    {
        var moved = _elsewhere.CreateDirectory("ProgramData");
        Directory.Delete(_system.ProgramData);
        Junction.ToDirectory(_system.ProgramData, moved);

        var policy = Policy();
        var inside = Path.Combine(moved, "Vendor", "settings.ini");

        Assert.False(policy.MayRemove(inside).IsAllowed);
        Assert.NotNull(policy.RefusedAtAndBelow(inside));
    }

    /// <summary>
    /// A link followed can refuse a removal and never allow one, so a permitting region is not
    /// followed. The signed-in profile moved by a junction into another account's profile does not
    /// carry its permission there.
    /// </summary>
    [Fact]
    public void APermittingRegionIsNotFollowedThroughALink()
    {
        var moved = _temp.CreateDirectory("another-account", "moved-profile");
        Directory.Delete(_environment.UserProfile, recursive: true);
        Junction.ToDirectory(_environment.UserProfile, moved);

        Assert.False(Policy().MayRemove(Path.Combine(moved, "notes.txt")).IsAllowed);
    }

    /// <summary>
    /// Fails closed. A folder above the item that Windows will not open hides where the removal
    /// would act, so the removal is refused rather than judged by its text.
    /// </summary>
    [Fact]
    public void AnItemBelowAFolderWindowsWillNotOpenIsRefused()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads");
        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [],
            new FakeVolumeInventory(),
            new UndescribableDirectoryFileSystem(WindowsFileSystem.Default, folder));

        var verdict = policy.MayRemove(Path.Combine(folder, "big.bin"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("would not open", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fails closed. A link to a volume mounted nowhere a user can name leads where no rule can be
    /// asked, so the removal is refused.
    /// </summary>
    [Fact]
    public void AnItemThroughALinkToAVolumeWithNoNameIsRefused()
    {
        var folder = _temp.CreateDirectory("profile", "Mounted");
        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(_system, _environment),
            [],
            new FakeVolumeInventory(),
            new RedirectingFileSystem(
                WindowsFileSystem.Default, folder, @"\\?\Volume{0e6b1d3a-1c2d-4e5f-8a9b-0c1d2e3f4a5b}\"));

        var verdict = policy.MayRemove(Path.Combine(folder, "big.bin"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("no letter or folder", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder above the item that is not there hides nothing, and the item is judged by its text
    /// as before: a row that vanished is reported as gone, not refused.
    /// </summary>
    [Fact]
    public void AnItemBelowAFolderThatIsNotThereIsJudgedByItsText()
    {
        var absent = Path.Combine(_environment.UserProfile, "gone", "big.bin");

        Assert.True(Policy().MayRemove(absent).IsAllowed);
    }

    /// <summary>
    /// §6.3 for where a link leads. The answer is a path Windows gives, so a deep tree would prove
    /// nothing; the form of the path handed to the filesystem is what discriminates.
    /// </summary>
    [Fact]
    public void AsksWhereALinkLeadsInExtendedLengthForm()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads");
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var policy = new ExploreActionPolicy([], [Gradle()], new FakeVolumeInventory(), recording);

        Assert.True(policy.MayRemove(Path.Combine(folder, "big.bin")).IsAllowed);
        Assert.Contains(LongPath.Extended(folder), recording.Paths);
        Assert.Contains(LongPath.Extended(GradleRoot), recording.Paths);
        Assert.All(recording.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    private ToolRoot Gradle() =>
        ToolRoot.Of(GradleRoot, "Gradle's own folder.", GradleCacheProvider.DisposableChildren);

    private ExploreActionPolicy Policy(params ToolRoot[] toolRoots) =>
        new(ProtectedRegions.For(_system, _environment), toolRoots, new FakeVolumeInventory());
}
