using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1's refusals where a region, a tool root or the item asked about is named through a letter
/// <c>subst</c> made for a folder. A declaration is named the way a tool or a setting names it, and
/// the item the way the user reached it, so each refusal here was missed when the two were compared
/// as text. Each sits beside the §5.6 half: an ordinary folder reached the same way stays removable.
/// </summary>
public sealed class ExploreActionPolicyAliasTests : IDisposable
{
    private const string Letter = @"S:\";

    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes;

    public ExploreActionPolicyAliasTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
        _volumes = new FakeVolumeInventory().Substituting(Letter, Source);
    }

    public void Dispose() => _temp.Dispose();

    private string Source => Path.Combine(_environment.UserProfile, "src");

    /// <summary>
    /// The case in the report: a vcpkg clone a tool reports through the letter refuses what it does
    /// not recognise when the item is reached through the folder the letter stands for.
    /// </summary>
    [Fact]
    public void AProbedRootNamedThroughALetterRefusesWhatIsInItReachedDirectly()
    {
        var policy = Policy(probedRoots: [Clone(@"S:\vcpkg")]);

        Assert.False(policy.MayRemove(Path.Combine(Source, "vcpkg", "installed")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Source, "vcpkg", "buildtrees")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Source, "other", "installed")).IsAllowed);
    }

    /// <summary>And the other way about: a root named directly, and the item reached through the letter.</summary>
    [Fact]
    public void AProbedRootNamedDirectlyRefusesWhatIsInItReachedThroughALetter()
    {
        var policy = Policy(probedRoots: [Clone(Path.Combine(Source, "vcpkg"))]);

        Assert.False(policy.MayRemove(@"S:\vcpkg\installed").IsAllowed);
        Assert.True(policy.MayRemove(@"S:\vcpkg\buildtrees").IsAllowed);
        Assert.True(policy.MayRemove(@"S:\other\installed").IsAllowed);
    }

    /// <summary>A declared root, which is pooled with the others at its depth, follows the letter too.</summary>
    [Fact]
    public void ADeclaredRootNamedThroughALetterRefusesWhatItDoesNotRecognise()
    {
        var policy = Policy(toolRoots: [ToolRoot.Folders(@"S:\tool", "The tool's own folder.", Named("cache"))]);

        Assert.False(policy.MayRemove(Path.Combine(Source, "tool")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Source, "tool", "config")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Source, "tool", "cache")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Source, "other", "config")).IsAllowed);
    }

    /// <summary>
    /// The innermost root decides, and innermost is by levels rather than by the length of a root's
    /// path. The inner root is named through the letter, which makes its path the shorter of the two,
    /// and read by length the outer root's recognition of <c>cache</c> answered for what is inside it.
    /// </summary>
    [Fact]
    public void TheInnermostRootDecidesWhereItIsNamedThroughALetter()
    {
        var outer = Path.Combine(Source, "tool");
        var policy = Policy(toolRoots:
        [
            ToolRoot.Folders(outer, "The tool's own folder.", Named("cache")),
            ToolRoot.Folders(@"S:\tool\cache", "The tool's cache.", Named("packages")),
        ]);

        // The premise: the inner root's path is the shorter, so length alone picks the outer one.
        Assert.True(@"S:\tool\cache".Length < outer.Length);

        Assert.False(policy.MayRemove(Path.Combine(outer, "cache", "credentials")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(outer, "cache", "packages")).IsAllowed);
    }

    /// <summary>
    /// Removing a folder that holds a refused location named through the letter takes it along, so it
    /// is refused. The location is named in the reason as the removal would reach it.
    /// </summary>
    [Fact]
    public void AFolderHoldingAProbedRootNamedThroughALetterIsRefused()
    {
        var clone = _temp.CreateDirectory("profile", "src", "vcpkg");
        var beside = _temp.CreateDirectory("profile", "elsewhere");
        var policy = Policy(probedRoots: [Clone(@"S:\vcpkg")]);

        var verdict = policy.MayRemove(Source);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(clone, verdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(policy.MayRemove(beside).IsAllowed);
    }

    /// <summary>
    /// A region named through a letter: the temporary folder at the top of a letter made for it, as
    /// <see cref="ProtectedRegions"/> names it from the environment. Added here rather than through
    /// the fake environment, which creates the folder it is given. Read by the length of each
    /// region's path, the profile's permission answered for the folder, because <c>T:\</c> is
    /// shorter than the profile's path.
    /// </summary>
    [Fact]
    public void TheTemporaryFolderNamedThroughALetterIsRefusedAtItsOwnPath()
    {
        var temp = Path.Combine(_environment.LocalAppData, "Temp");
        _volumes.Substituting(@"T:\", temp);

        var policy = new ExploreActionPolicy(
            [
                .. ProtectedRegions.For(_system, _environment),
                ProtectedRegion.Refusing(@"T:\", RegionScope.PathOnly, "This is your temporary folder."),
            ],
            [],
            _volumes);

        Assert.False(policy.MayRemove(temp).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(temp, "Some Program")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(_environment.LocalAppData, "Some Program")).IsAllowed);
    }

    private ExploreActionPolicy Policy(IEnumerable<ToolRoot>? toolRoots = null, IEnumerable<ToolRoot>? probedRoots = null) =>
        new(ProtectedRegions.For(_system, _environment), toolRoots ?? [], _volumes, probedRoots: probedRoots);

    private static ToolRoot Clone(string path) =>
        new(path, "The clone.", static child => child.Name.Equals("buildtrees", StringComparison.OrdinalIgnoreCase));

    private static Predicate<string> Named(string name) =>
        candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase);
}
