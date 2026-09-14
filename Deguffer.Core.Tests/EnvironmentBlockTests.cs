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

    /// <summary>The environment a process was handed, which Windows expanded before it ran.</summary>
    private static Dictionary<string, string> Variables(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Registry values of the kind Windows resolves, <c>REG_EXPAND_SZ</c>.</summary>
    private static Dictionary<string, EnvironmentValue> Expandable(params (string Name, string Value)[] entries) =>
        Registry(entries, expandable: true);

    /// <summary>
    /// Registry values of the other kind, <c>REG_SZ</c>, whose <c>%NAME%</c> is part of the value
    /// and reaches a program exactly as it was written.
    /// </summary>
    private static Dictionary<string, EnvironmentValue> Literal(params (string Name, string Value)[] entries) =>
        Registry(entries, expandable: false);

    private static Dictionary<string, EnvironmentValue> Registry(
        (string Name, string Value)[] entries,
        bool expandable) =>
        entries.ToDictionary(
            entry => entry.Name,
            entry => new EnvironmentValue(entry.Value, expandable),
            StringComparer.OrdinalIgnoreCase);

    private static EnvironmentBlock Started(
        Dictionary<string, string>? process = null,
        Dictionary<string, EnvironmentValue>? machine = null,
        Dictionary<string, EnvironmentValue>? user = null) =>
        EnvironmentBlock.Startup(process ?? Variables(), machine ?? Expandable(), user ?? Expandable());

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
            machine: Expandable(("Path", System32)));

        var refreshed = startup.Refresh(
            Expandable(("Path", System32)),
            Expandable(("Path", @"C:\Users\testuser\.pixi\bin")));

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
            machine: Expandable(("Path", System32)));

        var refreshed = startup.Refresh(Expandable(("Path", System32)), Expandable());

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
            Expandable(("Path", System32)),
            Expandable(("Path", @"C:\Users\testuser\bin")));

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
            machine: Expandable(("Path", System32)));

        var added = startup.Refresh(
            Expandable(("Path", System32)),
            Expandable(("Path", @"C:\Users\testuser\.pixi\bin")));

        // Composed over the start-up block again, exactly as UserEnvironment composes each pass.
        // Chaining this onto `added` instead is the defect the case is here to catch, and it would
        // carry the directory forward for the life of the process.
        var removed = startup.Refresh(Expandable(("Path", System32)), Expandable());

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
            Expandable(("PATHEXT", ".COM;.EXE;.MSC")),
            Expandable(("PATHEXT", ".COM;.EXE")));

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
            user: Expandable(("PLAYWRIGHT_BROWSERS_PATH", Configured)));

        var refreshed = startup.Refresh(Expandable(), Expandable());

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

        var refreshed = startup.Refresh(Expandable(), Expandable());

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

        var refreshed = startup.Refresh(Expandable(("Path", System32)), Expandable());

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
            user: Expandable(("PLAYWRIGHT_BROWSERS_PATH", @"C:\Users\testuser\AppData\Local\ms-playwright")));

        var refreshed = startup.Refresh(
            Expandable(),
            Expandable(("PLAYWRIGHT_BROWSERS_PATH", @"E:\relocated")));

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
            user: Expandable(("PLAYWRIGHT_BROWSERS_PATH", Original)));

        var refreshed = startup.Refresh(
            Expandable(),
            Expandable(("PLAYWRIGHT_BROWSERS_PATH", @"E:\relocated")));

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

        var refreshed = startup.Refresh(Expandable(), Expandable(("Path", @"%USERPROFILE%\.cargo\bin")));

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
            machine: Expandable(("ChocolateyInstall", @"C:\ProgramData\chocolatey")));

        var refreshed = startup.Refresh(
            Expandable(("ChocolateyInstall", @"D:\chocolatey")),
            Expandable(("Path", @"%ChocolateyInstall%\bin")));

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
            user: Expandable(("CARGO_HOME", @"C:\Users\testuser\.cargo")));

        var refreshed = startup.Refresh(
            Expandable(),
            Expandable(("CARGO_HOME", @"C:\Users\testuser\.cargo"), ("Path", @"%CARGO_HOME%\bin")));

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
        var refreshed = Started().Refresh(Expandable(), Expandable(("Path", @"%NOT_SET_ANYWHERE%\bin")));

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
        var refreshed = Started().Refresh(Expandable(), Expandable(("Path", @"%Path%;C:\tools")));

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
            machine: Expandable(("PATHEXT", ".COM;.EXE;.BAT")));

        var refreshed = startup.Refresh(Expandable(("PATHEXT", ".COM;.EXE;.BAT")), Expandable());

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

        var refreshed = startup.Refresh(Expandable(("Path", @"C:\tools")), Expandable());

        Assert.Equal([@"C:\tools\"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A <c>REG_SZ</c> cache variable reaches its tool with the <c>%NAME%</c> still in it, because
    /// that is what Windows hands the tool. Resolving it here would have Deguffer measure — and
    /// offer to empty — a real directory while the tool wrote somewhere else entirely, which is the
    /// §5.2 divergence the seam exists to prevent.
    /// </summary>
    [Fact]
    public void ALiteralValueKeepsTheNameItWasWrittenWith()
    {
        var startup = Started(process: Variables(("LOCALAPPDATA", @"C:\Users\testuser\AppData\Local")));

        var refreshed = startup.Refresh(
            Expandable(),
            Literal(("PLAYWRIGHT_BROWSERS_PATH", @"%LOCALAPPDATA%\ms-playwright")));

        Assert.Equal(@"%LOCALAPPDATA%\ms-playwright", refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// The same value stored as <c>REG_EXPAND_SZ</c>. The pair is the whole of the rule: the text
    /// is identical and the kind is the only thing that decides the answer.
    /// </summary>
    [Fact]
    public void AnExpandableValueResolvesTheNameItWasWrittenWith()
    {
        var startup = Started(process: Variables(("LOCALAPPDATA", @"C:\Users\testuser\AppData\Local")));

        var refreshed = startup.Refresh(
            Expandable(),
            Expandable(("PLAYWRIGHT_BROWSERS_PATH", @"%LOCALAPPDATA%\ms-playwright")));

        Assert.Equal(
            @"C:\Users\testuser\AppData\Local\ms-playwright",
            refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// A literal <c>PATH</c> entry is searched as written, so Deguffer looks where the shell looks.
    /// Expanding it adds a directory no command would resolve from, and a tool found there is a
    /// tool Deguffer reports installed when it is not.
    /// </summary>
    [Fact]
    public void ALiteralPathEntryIsSearchedExactlyAsItWasWritten()
    {
        var startup = Started(process: Variables(("USERPROFILE", @"C:\Users\testuser")));

        var refreshed = startup.Refresh(Expandable(), Literal(("Path", @"%USERPROFILE%\bin")));

        Assert.Equal([@"%USERPROFILE%\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// The two halves of <c>PATH</c> have their own kinds, and neither kind belongs to either half.
    /// One Windows 11 install was counted: 16 <c>REG_SZ</c> values to 5 <c>REG_EXPAND_SZ</c> in the
    /// machine key, and <c>Path</c> itself <c>REG_SZ</c> there while the user key held it as
    /// <c>REG_EXPAND_SZ</c>. The fixture below takes that arrangement the other way round, because
    /// both must work and the mirror case is the one no measurement here has seen. Joining the
    /// written forms and expanding the result gives one half the other half's treatment.
    /// </summary>
    [Fact]
    public void EachHalfOfPathIsExpandedByItsOwnKindBeforeTheyAreJoined()
    {
        var startup = Started(
            process: Variables(("SystemRoot", @"C:\Windows"), ("USERPROFILE", @"C:\Users\testuser")));

        var refreshed = startup.Refresh(
            Expandable(("Path", @"%SystemRoot%\system32")),
            Literal(("Path", @"%USERPROFILE%\bin")));

        Assert.Equal([System32, @"%USERPROFILE%\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// A <c>%NAME%</c> stands for the resolved value of that name, so naming a literal one does not
    /// resolve it by the back door. The <c>REG_SZ</c> value below reaches its tool with
    /// <c>%LOCALAPPDATA%</c> still in it, and so must the <c>REG_EXPAND_SZ</c> value written in
    /// terms of it — otherwise a provider is handed a real directory the tool never writes to, and
    /// measures and offers to empty it (§5.2).
    /// </summary>
    [Fact]
    public void AnExpandableValueNamingALiteralOneDoesNotResolveWhatTheLiteralHeld()
    {
        var startup = Started(process: Variables(("LOCALAPPDATA", @"C:\Users\testuser\AppData\Local")));

        var refreshed = startup.Refresh(
            Expandable(("PLAYWRIGHT_BROWSERS_PATH", @"%CACHE_ROOT%\ms-playwright")),
            Literal(("CACHE_ROOT", @"%LOCALAPPDATA%\caches")));

        Assert.Equal(@"%LOCALAPPDATA%\caches\ms-playwright", refreshed.Value("PLAYWRIGHT_BROWSERS_PATH"));
    }

    /// <summary>
    /// The other half of that rule: naming an expandable value gives its resolved form, however
    /// many steps the chain takes. A model that only resolved a value's own text would leave
    /// <c>%ProgramFiles%</c> in the answer and put a directory no tool uses on the search list.
    /// </summary>
    [Fact]
    public void AChainOfExpandableValuesResolvesWhole()
    {
        var startup = Started(process: Variables(("ProgramFiles", @"C:\Program Files")));

        var refreshed = startup.Refresh(
            Expandable(("CUDA_PATH", @"%ProgramFiles%\NVIDIA"), ("Path", @"%CUDA_PATH%\bin")),
            Expandable());

        Assert.Equal([@"C:\Program Files\NVIDIA\bin"], refreshed.PathDirectories);
    }

    /// <summary>
    /// <c>%Path%</c> written inside another variable stands for the whole composed search, both
    /// halves of it. Composing the two halves for <c>PATH</c> alone and leaving the lookup holding
    /// the user's half would silently drop the machine's directories from every variable derived
    /// from it — and mark that variable a process override for the life of the run, because it
    /// would no longer match the block Windows built.
    /// </summary>
    [Fact]
    public void AVariableWrittenInTermsOfPathGetsBothHalvesOfIt()
    {
        // Nothing in the start-up block: a name the launching process already had would be a
        // process override, and an override keeps its own value rather than following the registry.
        var startup = Started();

        var refreshed = startup.Refresh(
            Expandable(("Path", System32)),
            Expandable(("Path", @"C:\Users\testuser\bin"), ("TOOLKIT", @"%Path%;D:\extra")));

        Assert.Equal($@"{System32};C:\Users\testuser\bin;D:\extra", refreshed.Value("TOOLKIT"));
    }
}
