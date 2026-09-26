using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;
using Microsoft.Win32;

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
    /// That the two environment keys are actually read, and that what comes back is a <c>PATH</c> a
    /// command resolves on. No fake stands in for this one: it is the only assertion here that fails
    /// if the registry read is broken. Break it and every provider reports its tool absent, so
    /// Storage offers nothing and Explore refuses nothing — a failure that reads as a clean machine.
    ///
    /// <para><c>cmd</c> is the subject because <c>system32</c> is on the machine <c>PATH</c> of
    /// every Windows install, so nothing here depends on a toolchain being present.</para>
    ///
    /// <para><b>It does not prove the expansion.</b> Whether the machine key writes that directory
    /// as <c>%SystemRoot%\system32</c> or in full varies by install, so on a machine that writes it
    /// in full this passes with expansion removed entirely. <c>SystemRoot</c> is supplied for the
    /// installs that do write it as a variable; the expansion rules themselves are pinned in
    /// <see cref="EnvironmentBlockTests"/>, where the input can be stated rather than found.</para>
    /// </summary>
    [Fact]
    public void TheRealEnvironmentStillResolvesACommandOnTheMachinePath()
    {
        // The start-up block supplies no PATH at all, so the only route left to cmd is the real
        // registry read. It carries SystemRoot because that is the one name a machine PATH is
        // written in terms of, and it lives in neither key.
        var logon = Values(("SystemRoot", Environment.GetEnvironmentVariable("SystemRoot")!));

        var environment = new UserEnvironment(
            UserEnvironment.ReadMachineEnvironment,
            UserEnvironment.ReadUserEnvironment,
            EnvironmentBlock.Startup(logon, Expandable(), Expandable()));

        Assert.NotNull(environment.FindExecutable("cmd"));
    }

    private static Dictionary<string, string> Values(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same, as the two environment keys hand them over. <c>REG_EXPAND_SZ</c> throughout,
    /// because nothing here turns on the kind: the two are told apart in
    /// <see cref="EnvironmentBlockTests"/>, where the input can be stated.
    /// </summary>
    private static Dictionary<string, EnvironmentValue> Expandable(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(
            entry => entry.Name,
            entry => new EnvironmentValue(entry.Value, Expandable: true),
            StringComparer.OrdinalIgnoreCase);

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

        var user = Expandable();
        var environment = new UserEnvironment(
            () => Expandable(("PATHEXT", ".EXE")),
            () => user,
            EnvironmentBlock.Startup(Values(), Expandable(), Expandable()));

        Assert.Null(environment.FindExecutable("deguffer-fixture-tool"));

        // The installer's write. A running process is told nothing about it, so the answer must not
        // change until something asks the machine again.
        user["Path"] = new EnvironmentValue(installed, Expandable: true);

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
        const string Configured = @"C:\Users\testuser\AppData\Local\ms-playwright";

        var user = Expandable(("PLAYWRIGHT_BROWSERS_PATH", Configured));
        var environment = new UserEnvironment(
            () => Expandable(),
            () => user,
            EnvironmentBlock.Startup(
                Values(("PLAYWRIGHT_BROWSERS_PATH", Configured)),
                Expandable(),
                Expandable(("PLAYWRIGHT_BROWSERS_PATH", Configured))));

        user["PLAYWRIGHT_BROWSERS_PATH"] = new EnvironmentValue(@"D:\ms-playwright", Expandable: true);
        environment.Invalidate();

        Assert.Equal(@"D:\ms-playwright", environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// The temporary folder follows the variables that name it.
    ///
    /// <para>It has to, because two parts of the app read it by different routes:
    /// <c>ProtectedRegions</c> refuses <see cref="IUserEnvironment.TempPath"/> as the folder
    /// Explore may not remove, and <c>TempRoots</c> accepts what <c>TMP</c> and <c>TEMP</c> name as
    /// a root Storage empties. Leave this one fixed at start-up while those two answer from the
    /// machine, and a folder redirected while Deguffer is open becomes a scratch root Storage clears
    /// and an ordinary removable folder in Explore at the same moment (§5.3, §7.1).</para>
    /// </summary>
    [Fact]
    public void TheTemporaryFolderFollowsAMidSessionRedirection()
    {
        var redirected = _temp.CreateDirectory("redirected-temp");

        // TMP lives in HKCU\Environment, so the start-up block and the registry agree about it to
        // begin with. That is what makes the later write a change rather than a deletion.
        var user = Expandable(("TMP", _temp.Path));

        var environment = new UserEnvironment(
            () => Expandable(),
            () => user,
            EnvironmentBlock.Startup(
                Values(("TMP", _temp.Path)),
                Expandable(),
                Expandable(("TMP", _temp.Path))));

        Assert.Equal(_temp.Path, environment.TempPath, StringComparer.OrdinalIgnoreCase);

        user["TMP"] = new EnvironmentValue(redirected, Expandable: true);
        environment.Invalidate();

        Assert.Equal(redirected, environment.TempPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// That the registry read carries each value's kind rather than deciding it. Everything the
    /// composition rules do with the kind is asserted in <see cref="EnvironmentBlockTests"/> against
    /// values a test states directly, so nothing there would notice this method answering
    /// <c>REG_SZ</c> for the whole key — and Deguffer would then leave every relocated cache
    /// unresolved while believing it had read the machine correctly.
    ///
    /// <para><b>It writes a key, which every other test in the suite is spared.</b> There is no
    /// seam between this method and the registry, by design: the kind is what a reader of the real
    /// API brings back, so a fake standing in for the API would be asserting its own answer. The
    /// key is this class's registry counterpart of the temporary directory it already writes to —
    /// named for this run, deleted afterwards, swept by a later run if this one is killed before it
    /// can delete it, not an environment key, and read by nothing else.</para>
    /// </summary>
    [Fact]
    public void TheRegistryReadTellsTheTwoValueKindsApart()
    {
        // The same text under both kinds, so the kind is the only thing that can distinguish them.
        const string Written = @"%DEGUFFER_TEST_UNSET%\cache";

        using var scratch = new ScratchKey();

        scratch.Key.SetValue("Expanded", Written, RegistryValueKind.ExpandString);
        scratch.Key.SetValue("Written", Written, RegistryValueKind.String);

        // Not an environment value at all, and its decimal digits would read as a path.
        scratch.Key.SetValue("Number", 1, RegistryValueKind.DWord);

        var values = UserEnvironment.ReadEnvironmentKey(Registry.CurrentUser, scratch.Path);

        Assert.Equal(new EnvironmentValue(Written, Expandable: true), values["Expanded"]);
        Assert.Equal(new EnvironmentValue(Written, Expandable: false), values["Written"]);
        Assert.DoesNotContain("Number", values.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// That the subkey read names the keys under a key and not its values, and answers nothing for a
    /// key that is not there. It writes a key for the reason
    /// <see cref="TheRegistryReadTellsTheTwoValueKindsApart"/> gives.
    /// </summary>
    [Fact]
    public void TheSubkeyReadNamesKeysAndNotValues()
    {
        using var scratch = new ScratchKey();

        scratch.Key.CreateSubKey("Common 13.0").Dispose();
        scratch.Key.CreateSubKey(@"Common 25.0\Media Cache").Dispose();
        scratch.Key.SetValue("Common 26.0", "a value, not a key");

        Assert.Equal(
            ["Common 13.0", "Common 25.0"],
            UserEnvironment.Current.ReadCurrentUserRegistrySubKeyNames(scratch.Path).Order(StringComparer.Ordinal));
        Assert.Empty(UserEnvironment.Current.ReadCurrentUserRegistrySubKeyNames(scratch.Path + @"\Missing"));
    }
}
