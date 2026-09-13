using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real <see cref="UserEnvironment"/>, which every other test replaces with a fake.
///
/// <para>This is a runtime-assumption guard rather than a rule test, the same job
/// <c>LongPathTests</c> does. A provider's safety rules are proved against
/// <c>FakeUserEnvironment</c> precisely so they need no toolchain installed, but that leaves the
/// class which answers from the actual platform untested by anything — and one of its answers comes
/// from a P/Invoke that fails silently by returning null.</para>
///
/// <para><b>Nothing here asserts where a folder is.</b> A known folder can be relocated, and this
/// tier and <c>%LOCALAPPDATA%</c> are redirected through separate registry values, so an assertion
/// on the shape of a path would go red on a machine that is configured legitimately and running
/// correct code. What is asserted is only what a defect would change.</para>
/// </summary>
public sealed class UserEnvironmentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// LocalLow has no <see cref="Environment.SpecialFolder"/>, so it is resolved through
    /// <c>SHGetKnownFolderPath</c> from an instance initialiser that runs while
    /// <see cref="UserEnvironment.Current"/> is still being constructed. A static field holding the
    /// folder identifier would not be assigned yet at that moment, the call would fail with an
    /// empty identifier, and the singleton the application uses would report LocalLow as unknown
    /// for the life of the process — which reads exactly like a machine that has no LocalLow.
    ///
    /// <para><see cref="UserEnvironment.Current"/> specifically, not a fresh instance: the
    /// initialisation order this guards against only applies to the static field, so an assertion
    /// on <c>new UserEnvironment()</c> would pass with the defect present.</para>
    ///
    /// <para>Null is a legitimate answer from the platform where no user profile is loaded, and the
    /// interface says so. It is not a legitimate answer to a test run, which is a signed-in
    /// interactive session by construction.</para>
    /// </summary>
    [Fact]
    public void TheSingletonResolvesLocalLowRatherThanReportingItUnknown()
    {
        var environment = UserEnvironment.Current;

        Assert.NotNull(environment.LocalLowAppData);

        // A wrong folder identifier does not fail — it answers with a different real folder. So the
        // discriminating assertion is that the answer is none of the places already had, which
        // holds wherever the profile is and wherever any of its tiers has been redirected to.
        Assert.NotEqual(environment.LocalAppData, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(environment.RoamingAppData, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(environment.UserProfile, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The composition against the real registry, which no fake can stand in for. Reading the two
    /// environment keys unexpanded means this class resolves <c>%SystemRoot%</c> itself, and that
    /// variable is in neither key — it is a logon-time one, reached only through the fallback to the
    /// process. Get any of that wrong and every provider reports its tool absent, so Storage offers
    /// nothing and Explore refuses nothing: a failure that looks like a clean machine.
    ///
    /// <para><c>cmd</c> is the subject because the machine <c>PATH</c> names its directory as
    /// <c>%SystemRoot%\system32</c> on every Windows install, so the assertion exercises the read,
    /// the expansion and the search at once without depending on a toolchain being present.</para>
    /// </summary>
    [Fact]
    public void TheRealEnvironmentStillResolvesACommandOnTheMachinePath()
    {
        Assert.NotNull(UserEnvironment.Current.FindExecutable("cmd"));
    }

    /// <summary>
    /// The whole route, from the two environment keys through composition to a command resolved on
    /// disk: a tool installed while Deguffer is open is found once the pass that follows the install
    /// invalidates, and not before, because nothing else tells a running process the machine moved.
    ///
    /// <para>The registry is read through the seam rather than for real, because the assertion needs
    /// the machine's <c>PATH</c> to change mid-test and a suite may not do that to the machine it
    /// runs on (G8). What is real is the <c>PATH</c> search, the memoisation and the
    /// <see cref="UserEnvironment.Invalidate"/> that drops it.</para>
    /// </summary>
    [Fact]
    public void AToolInstalledAfterStartUpIsFoundOnceTheNextPassInvalidates()
    {
        var installed = _temp.CreateDirectory("tool-bin");
        File.WriteAllBytes(Path.Combine(installed, "deguffer-fixture-tool.exe"), new byte[64]);

        var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var environment = new UserEnvironment(
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATHEXT"] = ".EXE" },
            () => user,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Null(environment.FindExecutable("deguffer-fixture-tool"));

        // The installer's write. A running process is told nothing about it, so the answer must not
        // change until something asks the machine again.
        user["Path"] = installed;

        Assert.Null(environment.FindExecutable("deguffer-fixture-tool"));

        environment.Invalidate();

        Assert.Equal(
            Path.Combine(installed, "deguffer-fixture-tool.exe"),
            environment.FindExecutable("deguffer-fixture-tool"),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same route for a variable rather than a command, which is how several tools relocate a
    /// cache — and the case §5.2 cares about, because a provider told the old location measures and
    /// offers to empty a directory the tool has stopped using.
    /// </summary>
    [Fact]
    public void ARelocatedCacheVariableIsReadAgainOnTheNextPass()
    {
        var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = @"C:\Users\testuser\AppData\Local\ms-playwright",
        };

        var environment = new UserEnvironment(
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            () => user,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = @"C:\Users\testuser\AppData\Local\ms-playwright",
            });

        user["PLAYWRIGHT_BROWSERS_PATH"] = @"D:\ms-playwright";
        environment.Invalidate();

        Assert.Equal(@"D:\ms-playwright", environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
    }
}
