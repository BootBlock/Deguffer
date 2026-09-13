using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a running Deguffer makes of an environment that changed under it.
///
/// <para>The regression underneath every case here is one bug: the process keeps the block Windows
/// gave it at start-up, so a tool whose installer added a <c>PATH</c> directory was found by neither
/// page until the app was restarted, and Explore went on allowing the folders that tool's provider
/// protects (§7.1). Composition is asserted rather than the registry, because changing a real
/// machine's environment to test it is not something a suite may do (G8).</para>
/// </summary>
public sealed class EnvironmentBlockTests
{
    private const string System32 = @"C:\Windows\system32";

    private static Dictionary<string, string> Variables(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    private static EnvironmentBlock Started(
        Dictionary<string, string>? process = null,
        Dictionary<string, string>? machine = null,
        Dictionary<string, string>? user = null) =>
        EnvironmentBlock.Startup(process ?? Variables(), machine ?? Variables(), user ?? Variables());

    /// <summary>
    /// The bug itself. An installer writes its directory into <c>HKCU\Environment</c> and
    /// broadcasts a change no running process acts on, so a refresh that only dropped cached
    /// lookups would search the same directories over again and go on reporting the tool absent.
    /// </summary>
    [Fact]
    public void ADirectoryAnInstallerAddedToPathIsSearchedWithoutARestart()
    {
        var startup = Started(
            process: Variables(("Path", System32)),
            machine: Variables(("Path", System32)));

        var refreshed = startup.Refresh(
            Variables(("Path", System32)),
            Variables(("Path", @"C:\Users\testuser\.pixi\bin")));

        Assert.Contains(@"C:\Users\testuser\.pixi\bin", refreshed.PathDirectories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A directory only this process's <c>PATH</c> has — one a launching shell prepended, or one
    /// the session added after logon — survives the refresh. Recomposing <c>PATH</c> from the
    /// registry alone would drop it, which would take tools away rather than add them, and Explore
    /// would then stop refusing folders it had been refusing all along.
    /// </summary>
    [Fact]
    public void ADirectoryOnlyThisProcessHasSurvivesARefresh()
    {
        var startup = Started(
            process: Variables(("Path", $@"C:\workshop\bin;{System32}")),
            machine: Variables(("Path", System32)));

        var refreshed = startup.Refresh(Variables(("Path", System32)), Variables());

        Assert.Equal([@"C:\workshop\bin", System32], refreshed.PathDirectories);
    }

    /// <summary>
    /// Order is what decides which of two installations of a tool answers, so the directories this
    /// process has been resolving against keep their places and the registry's are appended. The
    /// user's <c>PATH</c> follows the machine's within that, as Windows composes it.
    /// </summary>
    [Fact]
    public void TheUserPathFollowsTheMachinePathAndBothFollowTheProcess()
    {
        var startup = Started(process: Variables(("Path", @"C:\session\bin")));

        var refreshed = startup.Refresh(
            Variables(("Path", System32)),
            Variables(("Path", @"C:\Users\testuser\bin")));

        Assert.Equal([@"C:\session\bin", System32, @"C:\Users\testuser\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A refresh is composed over the start-up block, never over the previous refresh, so a
    /// directory the user takes off <c>PATH</c> between two passes goes with it. Chaining would
    /// accumulate every state the machine had passed through and leave Deguffer finding tools by a
    /// route that no longer exists.
    /// </summary>
    [Fact]
    public void ADirectoryTakenOffPathAgainDoesNotLinger()
    {
        var startup = Started(
            process: Variables(("Path", System32)),
            machine: Variables(("Path", System32)));

        var added = startup.Refresh(
            Variables(("Path", System32)),
            Variables(("Path", @"C:\Users\testuser\.pixi\bin")));

        // Composed over the start-up block again, exactly as UserEnvironment composes each pass.
        // Chaining this onto `added` instead is the defect the case is here to catch, and it would
        // carry the directory forward for the life of the process.
        var removed = startup.Refresh(Variables(("Path", System32)), Variables());

        Assert.Contains(@"C:\Users\testuser\.pixi\bin", added.PathDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\testuser\.pixi\bin", removed.PathDirectories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user <c>PATHEXT</c> replaces the machine's rather than extending it. Windows appends only
    /// <c>PATH</c>, so treating <c>PATHEXT</c> the same way would have Deguffer report a tool
    /// present under an extension the user's own value removed, and the shell would then refuse to
    /// run the command Deguffer said was there.
    /// </summary>
    [Fact]
    public void AUserPathExtReplacesTheMachineOneRatherThanExtendingIt()
    {
        var startup = Started(process: Variables(("PATHEXT", ".COM")));

        var refreshed = startup.Refresh(
            Variables(("PATHEXT", ".COM;.EXE;.MSC")),
            Variables(("PATHEXT", ".COM;.EXE")));

        Assert.Equal([".COM", ".EXE"], refreshed.PathExtensions);
    }

    /// <summary>
    /// A variable the process took from the registry and the registry has since lost is gone, not
    /// merely unchanged. A restarted Deguffer would not see it, and a provider still told where a
    /// relocated cache is goes on measuring, and offering to empty, a directory its tool has
    /// stopped pointing at (§5.2).
    /// </summary>
    [Fact]
    public void AVariableDeletedFromTheRegistryStopsBeingReported()
    {
        const string Configured = @"C:\Users\testuser\AppData\Local\ms-playwright";

        var startup = Started(
            process: Variables(("PLAYWRIGHT_BROWSERS_PATH", Configured)),
            user: Variables(("PLAYWRIGHT_BROWSERS_PATH", Configured)));

        var refreshed = startup.Refresh(Variables(), Variables());

        Assert.Null(refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// The other side of that removal: a variable the registry never held is the launching
    /// process's own, so an empty registry is not news about it. Dropping every name the registry
    /// does not currently list would take the shell's choice away on the first refresh.
    /// </summary>
    [Fact]
    public void AVariableTheRegistryNeverHeldSurvivesAnEmptyRegistry()
    {
        var startup = Started(process: Variables(("PLAYWRIGHT_BROWSERS_PATH", @"D:\browsers")));

        var refreshed = startup.Refresh(Variables(), Variables());

        Assert.Equal(@"D:\browsers", refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// <c>PATH</c> read as a variable and <c>PATH</c> as the list of directories searched are one
    /// fact. Composing the variable from the registry alone would have it name directories
    /// <see cref="EnvironmentBlock.PathDirectories"/> does not visit and omit the ones it does, so a
    /// provider reading the variable and the search that resolves its tool would disagree about the
    /// same machine.
    /// </summary>
    [Fact]
    public void TheVariableAndTheSearchedDirectoriesAgree()
    {
        var startup = Started(process: Variables(("Path", @"C:\session\bin")));

        var refreshed = startup.Refresh(Variables(("Path", System32)), Variables());

        Assert.Equal(
            refreshed.PathDirectories,
            (refreshed.Value("Path") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A variable the launching process set to something of its own is a deliberate choice about
    /// this session — a shell that exports <c>PLAYWRIGHT_BROWSERS_PATH</c> before starting Deguffer
    /// is saying which browsers this run is about. Overwriting it from the registry would send the
    /// provider to measure, and offer to empty, a cache the user is not using.
    /// </summary>
    [Fact]
    public void AVariableTheLaunchingProcessSetKeepsItsValue()
    {
        var startup = Started(
            process: Variables(("PLAYWRIGHT_BROWSERS_PATH", @"D:\browsers")),
            user: Variables(("PLAYWRIGHT_BROWSERS_PATH", @"C:\Users\testuser\AppData\Local\ms-playwright")));

        var refreshed = startup.Refresh(
            Variables(),
            Variables(("PLAYWRIGHT_BROWSERS_PATH", @"E:\relocated")));

        Assert.Equal(@"D:\browsers", refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// The other half of that rule, and the half the issue is about: a variable the process merely
    /// inherited follows the registry. Without it, relocating a cache while Deguffer is open leaves
    /// every provider measuring where the cache used to be.
    /// </summary>
    [Fact]
    public void AVariableTheProcessOnlyInheritedFollowsTheRegistry()
    {
        const string Original = @"C:\Users\testuser\AppData\Local\ms-playwright";

        var startup = Started(
            process: Variables(("PLAYWRIGHT_BROWSERS_PATH", Original)),
            user: Variables(("PLAYWRIGHT_BROWSERS_PATH", Original)));

        var refreshed = startup.Refresh(
            Variables(),
            Variables(("PLAYWRIGHT_BROWSERS_PATH", @"E:\relocated")));

        Assert.Equal(@"E:\relocated", refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// <c>USERPROFILE</c>, <c>SystemRoot</c> and the rest of the logon-time variables are in
    /// neither environment key, and a great many <c>PATH</c> entries are written in terms of them.
    /// Reading the registry unexpanded and then failing to resolve them would put a literal
    /// <c>%USERPROFILE%\.cargo\bin</c> on the search list, which matches nothing.
    /// </summary>
    [Fact]
    public void ALogonVariableThatOnlyTheProcessKnowsStillExpands()
    {
        var startup = Started(process: Variables(("USERPROFILE", @"C:\Users\testuser")));

        var refreshed = startup.Refresh(Variables(), Variables(("Path", @"%USERPROFILE%\.cargo\bin")));

        Assert.Equal([@"C:\Users\testuser\.cargo\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// An entry written in terms of a variable the installer changed in the same visit follows the
    /// new value. Expanding against this process's block instead would resolve the move by half:
    /// the variable would read as relocated while the <c>PATH</c> entry derived from it still
    /// pointed at the old tree.
    /// </summary>
    [Fact]
    public void AnEntryExpandsAgainstTheRegistryAsItNowStands()
    {
        var startup = Started(
            process: Variables(("ChocolateyInstall", @"C:\ProgramData\chocolatey")),
            machine: Variables(("ChocolateyInstall", @"C:\ProgramData\chocolatey")));

        var refreshed = startup.Refresh(
            Variables(("ChocolateyInstall", @"D:\chocolatey")),
            Variables(("Path", @"%ChocolateyInstall%\bin")));

        Assert.Equal([@"D:\chocolatey\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A process override reaches the entries written in terms of it as well, so what
    /// <c>CARGO_HOME</c> reports and where <c>%CARGO_HOME%\bin</c> resolves to cannot disagree.
    /// A rule applied to the variable but not to its uses is the kind that fails silently.
    /// </summary>
    [Fact]
    public void AnEntryExpandsAgainstAProcessOverrideOfTheNameItUses()
    {
        var startup = Started(
            process: Variables(("CARGO_HOME", @"D:\cargo")),
            user: Variables(("CARGO_HOME", @"C:\Users\testuser\.cargo")));

        var refreshed = startup.Refresh(
            Variables(),
            Variables(("CARGO_HOME", @"C:\Users\testuser\.cargo"), ("Path", @"%CARGO_HOME%\bin")));

        Assert.Equal(@"D:\cargo", refreshed.Value("CARGO_HOME"));
        Assert.Equal([@"D:\cargo\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A name nothing resolves is left exactly as written, which is what
    /// <c>ExpandEnvironmentStrings</c> does. Dropping it would turn the entry into the relative path
    /// <c>\bin</c>, and a relative entry on <c>PATH</c> resolves against the current directory —
    /// so a command would be looked for somewhere the user never put one.
    /// </summary>
    [Fact]
    public void ANameNothingResolvesIsLeftAsItWasWritten()
    {
        var refreshed = Started().Refresh(Variables(), Variables(("Path", @"%NOT_SET_ANYWHERE%\bin")));

        Assert.Equal([@"%NOT_SET_ANYWHERE%\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A value that names itself terminates rather than growing until the process runs out of
    /// memory. <c>PATH=%PATH%;…</c> is a real thing to find in a registry key, written by a script
    /// that meant it to be expanded at the moment it was set.
    /// </summary>
    [Fact]
    public void AValueThatNamesItselfDoesNotExpandForever()
    {
        var refreshed = Started().Refresh(Variables(), Variables(("Path", @"%Path%;C:\tools")));

        Assert.Contains(@"C:\tools", refreshed.PathDirectories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows adds <c>.CPL</c> to a session's <c>PATHEXT</c> without writing it to either key, so
    /// recomposing the list from the registry alone would quietly shorten it. It is a joined
    /// variable for the reason <c>PATH</c> is: what the process has is added to, never replaced.
    /// </summary>
    [Fact]
    public void AnExtensionOnlyTheSessionHasSurvivesARefresh()
    {
        var startup = Started(
            process: Variables(("PATHEXT", ".COM;.EXE;.BAT;.CPL")),
            machine: Variables(("PATHEXT", ".COM;.EXE;.BAT")));

        var refreshed = startup.Refresh(Variables(("PATHEXT", ".COM;.EXE;.BAT")), Variables());

        Assert.Contains(".CPL", refreshed.PathExtensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A trailing separator does not make a second entry for the same directory. Windows tolerates
    /// both forms in a <c>PATH</c>, and a duplicate would have every absent tool probed twice in the
    /// same folder on every pass.
    /// </summary>
    [Fact]
    public void ATrailingSeparatorDoesNotMakeADuplicateEntry()
    {
        var startup = Started(process: Variables(("Path", @"C:\tools\")));

        var refreshed = startup.Refresh(Variables(("Path", @"C:\tools")), Variables());

        Assert.Equal([@"C:\tools\"], refreshed.PathDirectories);
    }
}
