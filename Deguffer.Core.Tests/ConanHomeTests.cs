using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Conan's home as <c>get_conan_user_home</c> in <c>conan/internal/paths.py</c> works it out: a
/// <c>.conanrc</c> above the folder Conan runs in, then <c>CONAN_HOME</c>, then <c>~/.conan2</c>,
/// with every quirk of Conan's reading kept, because a home read differently from Conan's is one
/// Deguffer checks while Conan creates another.
/// </summary>
public sealed class ConanHomeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ConanHomeTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>The folder above the one tools run in, so a <c>.conanrc</c> here is found by walking up.</summary>
    private string Parent => Path.Combine(_environment.LocalAppData, "Deguffer");

    private string WorkingDirectory => Path.Combine(Parent, "tool-working-directory");

    private ConanHomeAnswer Resolve() => Resolve(new ConanRcWalk(_temp.Path));

    private ConanHomeAnswer Resolve(ConanRcWalk walk) => ConanHome.Resolve(_environment, WorkingDirectory, walk);

    private void WriteConanRc(string text)
    {
        Directory.CreateDirectory(Parent);
        File.WriteAllText(Path.Combine(Parent, ".conanrc"), text);
    }

    private string FoundHome() => Assert.IsType<ConanHomeAnswer.Found>(Resolve()).Home;

    [Fact]
    public void DefaultsToConan2InTheProfile() =>
        Assert.Equal(Path.Combine(_environment.UserProfile, ".conan2"), FoundHome());

    [Fact]
    public void ReadsConanHome()
    {
        var home = Path.Combine(_temp.Path, "conan-home");
        _environment.WithEnvironmentVariable("CONAN_HOME", home);

        Assert.Equal(home, FoundHome());
    }

    [Fact]
    public void ExpandsALeadingTildeToTheProfile()
    {
        _environment.WithEnvironmentVariable("CONAN_HOME", "~/conan-home");

        Assert.Equal(Path.Combine(_environment.UserProfile, "conan-home"), FoundHome());
    }

    [Theory]
    [InlineData("conan-home")]
    [InlineData("~otheruser/conan-home")]
    [InlineData(@"\conan-home")]
    [InlineData("")]
    public void LeavesAHomeConanWouldRefuseUnknown(string value)
    {
        _environment.WithEnvironmentVariable("CONAN_HOME", value);

        Assert.IsType<ConanHomeAnswer.Unknown>(Resolve());
    }

    [Theory]
    [InlineData("conan_home=./conan-home")]
    [InlineData("conan_home=.\\conan-home")]
    [InlineData("# a comment\r\nconan_home=./conan-home\r\n")]
    [InlineData("other=value\nconan_home=./conan-home")]
    public void ResolvesARelativeConanRcValueAgainstItsFolder(string text)
    {
        WriteConanRc(text);

        Assert.Equal(Path.Combine(Parent, "conan-home"), FoundHome());
    }

    [Fact]
    public void ResolvesAParentRelativeConanRcValue()
    {
        WriteConanRc(@"conan_home=..\conan-home");

        Assert.Equal(Path.Combine(_environment.LocalAppData, "conan-home"), FoundHome());
    }

    [Fact]
    public void AConanRcWinsOverConanHome()
    {
        var absolute = Path.Combine(_temp.Path, "from-conanrc");
        WriteConanRc($"conan_home={absolute}");
        _environment.WithEnvironmentVariable("CONAN_HOME", Path.Combine(_temp.Path, "from-variable"));

        Assert.Equal(absolute, FoundHome());
    }

    /// <summary>A missing key, an empty value and a key with a space are all no home to Conan.</summary>
    [Theory]
    [InlineData("other=value")]
    [InlineData("conan_home=")]
    [InlineData("conan_home =./conan-home")]
    public void FallsThroughToConanHomeWhereTheConanRcNamesNoHome(string text)
    {
        var home = Path.Combine(_temp.Path, "from-variable");
        WriteConanRc(text);
        _environment.WithEnvironmentVariable("CONAN_HOME", home);

        Assert.Equal(home, FoundHome());
    }

    /// <summary>Conan stops with an error on each of these, so nothing is assumed about its home.</summary>
    [Theory]
    [InlineData("conan_home=./a\n\nother=b")]
    [InlineData("conan_home")]
    [InlineData("conan_home=./a=b")]
    [InlineData("conan_home=conan-home")]
    [InlineData("conan_home=./café")]
    public void LeavesTheHomeUnknownWhereTheConanRcCannotBeReadAsConanReadsIt(string text)
    {
        WriteConanRc(text);

        Assert.IsType<ConanHomeAnswer.Unknown>(Resolve());
    }

    [Fact]
    public void ReadsAConanRcWhoseExistenceWindowsWillNotDescribeAsRefused()
    {
        WriteConanRc("conan_home=./conan-home");
        var conanrc = Path.Combine(Parent, ".conanrc");

        using (DeniedDirectory.WithUnreadableFile(conanrc))
        {
            Assert.Equal(conanrc, Assert.IsType<ConanHomeAnswer.Refused>(Resolve()).Path);
        }
    }

    /// <summary>
    /// The walk ends at the folder it is given, which it still searches, so a test's walk never leaves
    /// the folder the test made and never reads a <c>.conanrc</c> the machine has above it.
    /// </summary>
    [Fact]
    public void TheWalkSearchesItsEndAndNothingAboveIt()
    {
        var end = _environment.LocalAppData;
        File.WriteAllText(Path.Combine(_environment.UserProfile, ".conanrc"), "conan_home=./above-the-end");

        Assert.Equal(Path.Combine(_environment.UserProfile, ".conan2"), Assert.IsType<ConanHomeAnswer.Found>(Resolve(new ConanRcWalk(end))).Home);

        File.WriteAllText(Path.Combine(end, ".conanrc"), "conan_home=./at-the-end");

        Assert.Equal(Path.Combine(end, "at-the-end"), Assert.IsType<ConanHomeAnswer.Found>(Resolve(new ConanRcWalk(end))).Home);
    }

    /// <summary>
    /// Conan reads a <c>.conanrc</c> of any size, and Deguffer reads one no further than a bound, so a
    /// larger one leaves the home unknown rather than read as naming nothing.
    /// </summary>
    [Fact]
    public void LeavesTheHomeUnknownWhereTheConanRcIsPastTheBound()
    {
        WriteConanRc("conan_home=./conan-home\n" + new string('#', 64 * 1024));

        Assert.IsType<ConanHomeAnswer.Unknown>(Resolve());
    }
}
