using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Caches and scratch that developer tools write into the temporary folder and never clear: Node's
/// module compile cache, the per-run folders Flutter and Dart's test runner leave when they are
/// killed, Firefox's anonymous temporary files, and the analyzer copies Roslyn makes for each
/// session (about 2 GB across them on the machine that prompted this).
///
/// <para><b>Tier 1, because each tool rebuilds or no longer reads what is offered.</b> Node's
/// documentation says so of its cache in as many words. A Flutter or test-runner folder belongs to a
/// run that has ended, Firefox deletes its own folder once it is idle, and a Roslyn session's copies
/// are of analyzers that still sit where they were copied from.</para>
///
/// <para><b>Each marker carries the live check its tool allows, and none relies on age.</b> Roslyn
/// holds a mutex named after each session folder for as long as the session lives and judges its
/// own folders stale by that mutex's absence, so Deguffer asks the same question. A Flutter folder's
/// name is a random number that nothing ties to a process, so every one of them is left alone while
/// the Dart VM that runs the tool is running. Node reads its cache whole and writes it only on exit,
/// and a missing file is a cache miss rather than a failure, so it needs no check.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> None of these tools has a command that clears what it
/// left in the temporary folder. Node's documentation names removing the directory as the way to
/// clean the cache.</para>
/// </summary>
public sealed partial class TempToolCacheProvider : TempMarkerProviderBase
{
    /// <summary>
    /// The variable that moves Node's compile cache out of the temporary folder. Where it is set, the
    /// cache there is examined as Node's own folder, recognising only its per-version directories.
    /// </summary>
    public const string NodeCompileCacheVariable = "NODE_COMPILE_CACHE";

    private const string NodeTool = "Node.js compile cache";
    private const string FlutterTool = "Flutter tool";
    private const string DartTestTool = "Dart test runner";
    private const string FirefoxTool = "Firefox";
    private const string RoslynTool = "Roslyn";
    private const string CompilerServerTool = "the C# and Visual Basic compiler server";

    private const string NodeReason =
        "Compiled code Node.js keeps so modules load faster. Node's documentation says to remove the "
        + "directory to clean it up, and rebuilds it the next time it is used.";

    private const string RoslynReason =
        "Copies of analyzers Roslyn made for an editing session that has ended. The originals are "
        + "where they always were, and the next session copies what it needs.";

    /// <summary>The directory Node's documentation names under the temporary folder.</summary>
    [GeneratedRegex(@"\Anode-compile-cache\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodeCompileCache();

    /// <summary>
    /// One Node build's cache inside a configured root: the version, an optional pre-release tag, the
    /// architecture and V8's cache version tag in hexadecimal. Windows adds no user suffix.
    ///
    /// <para>Eight hexadecimal digits, which is every tag observed. A tag written with fewer is left
    /// alone, which costs one version's cache rather than a directory that only resembles one.</para>
    /// </summary>
    [GeneratedRegex(
        @"\Av\d+\.\d+\.\d+(?:-[0-9a-z.]+)?-(?:x64|arm64|ia32)-[0-9a-f]{8}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodeVersionCache();

    /// <summary>
    /// What Dart's <c>createTempSync</c> makes on Windows from the tool's prefix: an unpadded
    /// hexadecimal number, or a GUID where that name was taken.
    /// </summary>
    [GeneratedRegex(
        @"\Aflutter_tools\.(?:[0-9a-f]{1,8}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlutterTools();

    /// <summary>The folder package:test makes for a run, on the same terms as <see cref="FlutterTools"/>.</summary>
    [GeneratedRegex(
        @"\Adart_test\.(?:[0-9a-f]{1,8}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DartTest();

    [GeneratedRegex(@"\Amozilla-temp-files\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MozillaTempFiles();

    /// <summary>A Roslyn session: a GUID in lowercase hexadecimal with no separators.</summary>
    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoslynSession();

    private readonly INamedMutexes _mutexes;
    private readonly TempMarker[] _topLevel;
    private readonly TempMarker _roslynSession;
    private readonly TempMarker _compilerSession;

    private static readonly TempMarker NodeVersion = new(
        NodeTool, NodeVersionCache(), TargetKind.Directory, NodeReason);

    public TempToolCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null,
        INamedMutexes? mutexes = null)
        : base(environment, runner, inspector, scanner, system, liveTrees)
    {
        _mutexes = mutexes ?? NamedMutexes.Default;

        _roslynSession = Session(RoslynTool);
        _compilerSession = Session(CompilerServerTool);

        _topLevel =
        [
            new(NodeTool, NodeCompileCache(), TargetKind.Directory, NodeReason),
            new(
                FlutterTool,
                FlutterTools(),
                TargetKind.Directory,
                "A folder the Flutter tool made for one run and would have removed on exit. It was left "
                + "because the run was killed, and nothing reads it again.")
            {
                // The tool runs in the Dart VM, and the tests it starts in the Flutter tester.
                HeldBy = ["dart", "flutter_tester"],
            },
            new(
                DartTestTool,
                DartTest(),
                TargetKind.Directory,
                "A folder Dart's test runner made for one run and would have removed on exit. It was "
                + "left because the run was killed, and nothing reads it again.")
            {
                HeldBy = ["dart"],
            },
            new(
                FirefoxTool,
                MozillaTempFiles(),
                TargetKind.Directory,
                "Firefox's own temporary files for media, printing and the clipboard. Firefox deletes "
                + "this folder itself once it has been idle for a while, and makes it again when needed.")
            {
                // Gecko's code, which the other browsers built on it share. Holding back for one that
                // does not write here costs nothing; missing one that does costs a print job.
                HeldBy = ["firefox", "thunderbird", "librewolf", "waterfox"],
            },
        ];
    }

    public override string Id => "temp-tool-caches";

    public override string Name => "Tool caches in temporary folders";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Node.js compiles the modules it loads again and caches them afresh, and Roslyn copies the "
        + "analyzers it needs for the next editing session. Nothing that is running is affected: "
        + "anything a running Flutter, Dart or Firefox may be using, and any Roslyn session that is "
        + "still open, is left where it is.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Node.js, Flutter, Dart, Firefox and Roslyn, the compiler behind C# and Visual Basic",
        Publisher = "the OpenJS Foundation, Google, Mozilla and Microsoft",
        Purpose = "Each of these writes a cache or a working folder into your temporary folder. Node "
            + "keeps compiled code there with nothing to limit its size, and the others make a folder "
            + "for each run or session and leave it behind whenever that run is killed rather than "
            + "finishing.",
        Recommendation = "Deguffer offers only folders these tools' own names identify, and leaves "
            + "alone anything one of them may still be using. Everything else in the temporary folder "
            + "is left to the Temporary files row.",
    };

    protected override string NothingLeftBehind =>
        "None of these tools has left a cache or a working folder in a temporary folder.";

    protected override IReadOnlyList<TempMarkerPlace> PlacesIn(IReadOnlyList<string> accountFolders)
    {
        var places = new List<TempMarkerPlace>();

        foreach (var folder in accountFolders)
        {
            places.Add(new TempMarkerPlace(folder, _topLevel));
            places.AddRange(SessionPlaces(folder, "Roslyn", RoslynTool, _roslynSession));
            places.AddRange(SessionPlaces(folder, "VBCSCompiler", CompilerServerTool, _compilerSession));
        }

        if (ConfiguredNodeCache(accountFolders) is { } configured)
        {
            places.Add(new TempMarkerPlace(configured, [NodeVersion], Owner: "Node.js"));
        }

        return places;
    }

    /// <summary>
    /// Where <see cref="NodeCompileCacheVariable"/> moves Node's cache, or null where it is not set,
    /// is not a full path, or names the directory the temporary-folder marker already covers.
    ///
    /// <para>Examined as Node's folder rather than taken whole. The variable is something anything on
    /// the machine may have written, and pointing it at a folder that holds anything else would
    /// otherwise offer that folder. Only the per-version directories Node makes are recognised in it,
    /// so the worst a misdirected setting costs is nothing.</para>
    /// </summary>
    private string? ConfiguredNodeCache(IReadOnlyList<string> accountFolders)
    {
        if (LongPath.Configured(Environment.GetEnvironmentVariable(NodeCompileCacheVariable)) is not { } configured)
        {
            return null;
        }

        var canonical = LongPath.Canonical(configured);

        return accountFolders.Any(folder => LongPath.Canonical(Path.Combine(folder, "node-compile-cache"))
                .Equals(canonical, StringComparison.OrdinalIgnoreCase))
            ? null
            : configured;
    }

    /// <summary>
    /// A shadow-copy tool's folder and the two layouts it has kept sessions in: the one Visual Studio
    /// ships today, and the one Roslyn's main branch moved to in August 2026. The tool's folder and
    /// each directory on the way down are its own, so what the markers do not name there is asserted
    /// to survive.
    ///
    /// <para>Innermost first, so a folder that is both a place and an entry of the one above it is
    /// named for what it is rather than as something unrecognised.</para>
    /// </summary>
    private static IEnumerable<TempMarkerPlace> SessionPlaces(string folder, string name, string tool, TempMarker session)
    {
        var root = Path.Combine(folder, name);
        var resolver = Path.Combine(root, "AnalyzerPathResolver");

        yield return new TempMarkerPlace(Path.Combine(resolver, "v1", "shadow"), [session], tool, folder);
        yield return new TempMarkerPlace(Path.Combine(resolver, "v1"), [], tool, folder);
        yield return new TempMarkerPlace(resolver, [], tool, folder);
        yield return new TempMarkerPlace(Path.Combine(root, "AnalyzerAssemblyLoader"), [session], tool, folder);
        yield return new TempMarkerPlace(root, [], tool, folder);
    }

    /// <summary>
    /// A session folder, live while the mutex named after it exists. Roslyn's own clean-up asks
    /// exactly this before it deletes a session, so the two cannot disagree about which are dead.
    /// </summary>
    private TempMarker Session(string tool) =>
        new(tool, RoslynSession(), TargetKind.Directory, RoslynReason)
        {
            InUse = _mutexes.Exists,
            InUseReason = "An editing session is still using these analyzer copies, so they are left alone.",
        };
}
