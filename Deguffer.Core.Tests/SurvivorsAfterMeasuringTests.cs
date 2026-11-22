using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every row that asks which of its spared children another row offers measures its own targets
/// while that question is answered. The first such question in a pass reads every row's
/// declarations, which takes seconds, and a row that awaited it before measuring would hold its
/// planning slot idle for that time and end at the reading's end.
///
/// <para>Each case holds the reading on a gate until the row has started measuring, which fails
/// where the row asks first: the measurement never starts, and the wait runs out. Once the gate
/// opens, a child no row offers is still asserted to survive, and a child another row offers is
/// still left out of the survivors.</para>
/// </summary>
public sealed class SurvivorsAfterMeasuringTests : IDisposable
{
    private const string Unrecognised = "deguffer-unrecognised";
    private const string Offered = "deguffer-offered";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>What a case is built over: a fresh fake profile, a scanner that says when a path is measured, and the declarations it asks.</summary>
    public sealed record Setup(FakeUserEnvironment Environment, FakeDirectoryScanner Scanner, RowDeclarations Declarations)
    {
        public FakeSystemDirectories System => new(Path.Combine(Path.GetDirectoryName(Environment.UserProfile)!, "system"));
    }

    /// <summary>
    /// A row, one directory it measures, and the folder whose children it classifies, where the test
    /// puts one child no row offers and one another row offers.
    /// </summary>
    public sealed record Built(ICleanupProvider Row, string Target, string Parent);

    private static string CreateCache(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[4096]);
        return path;
    }

    private static readonly Dictionary<string, Func<Setup, Built>> Cases = new()
    {
        ["Gradle"] = setup =>
        {
            var home = Path.Combine(setup.Environment.UserProfile, ".gradle");

            return new Built(
                new GradleCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                CreateCache(Path.Combine(home, "caches")),
                home);
        },
        ["Ivy"] = setup =>
        {
            var home = Path.Combine(setup.Environment.UserProfile, ".ivy2");

            return new Built(
                new IvyCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                CreateCache(Path.Combine(home, "cache")),
                home);
        },
        ["Dart analysis server"] = setup =>
        {
            var root = Path.Combine(setup.Environment.LocalAppData, ".dartServer");

            return new Built(
                new DartAnalysisServerProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                CreateCache(Path.Combine(root, ".analysis-driver")),
                root);
        },
        ["pipenv"] = setup =>
        {
            // Outside pip's three folders, so it is removed by path whether or not pip is found.
            var root = Path.Combine(setup.Environment.LocalAppData, "pipenv", "pipenv", "Cache");

            return new Built(
                new PipenvCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                CreateCache(Path.Combine(root, "pipenv-manifests")),
                root);
        },
        ["Coursier"] = setup =>
        {
            var cache = Path.Combine(setup.Environment.LocalAppData, "Coursier", "Cache", "v1");

            return new Built(
                new CoursierCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                CreateCache(Path.Combine(cache, "https")),
                cache);
        },
        ["Chromium"] = setup =>
        {
            // Its own profile, so the data folder itself is the level walked.
            var application = Path.Combine(setup.Environment.RoamingAppData, "Chatter");
            Directory.CreateDirectory(application);
            File.WriteAllText(Path.Combine(application, "Local State"), LocalState);

            return new Built(
                new ChromiumCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, liveTrees: FakeLiveTreeInspector.NothingLive,
                    discovery: new ChromiumUserDataDiscovery(setup.Environment), declarations: setup.Declarations),
                CreateCache(Path.Combine(application, "GPUCache")),
                application);
        },
        ["Epic web cache"] = setup =>
        {
            var webCache = Path.Combine(setup.Environment.LocalAppData, "EpicGamesLauncher", "Saved", "webcache_4430");

            return new Built(
                new EpicLauncherWebCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                CreateCache(Path.Combine(webCache, "Cache")),
                webCache);
        },
        ["VS Code cache"] = setup =>
        {
            var editor = CreateEditor(setup.Environment);

            return new Built(
                new VsCodeCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                CreateCache(Path.Combine(editor, "CachedData")),
                editor);
        },
        ["VS Code logs"] = setup =>
        {
            var editor = CreateEditor(setup.Environment);
            var logs = Path.Combine(editor, "logs");
            CreateCache(Path.Combine(logs, "20260101T090000"));

            return new Built(
                new VsCodeLogProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                logs,
                editor);
        },
        ["Kopia"] = setup =>
        {
            var cache = Path.Combine(setup.Environment.LocalAppData, "kopia", "0123456789abcdef");

            return new Built(
                new KopiaCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                CreateCache(Path.Combine(cache, "contents")),
                cache);
        },
        ["restic, by path"] = setup =>
        {
            // restic is not on the path, so each repository's cache is walked and removed by path.
            var repository = CreateResticRepository(setup.Environment);

            return new Built(
                new ResticCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                Path.Combine(repository, "data"),
                repository);
        },
        ["restic, by its own command"] = setup =>
        {
            // The folder's own children are what is asked about here, and its repository is what is measured.
            var environment = setup.Environment.WithExecutable("restic");
            var repository = CreateResticRepository(environment);

            return new Built(
                new ResticCacheProvider(
                    environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, system: setup.System, declarations: setup.Declarations),
                repository,
                Path.GetDirectoryName(repository)!);
        },
        ["Poetry"] = setup =>
        {
            var environment = setup.Environment.WithExecutable("poetry");
            var poetry = environment.FindExecutable("poetry")!;
            var cache = Path.Combine(environment.LocalAppData, "pypoetry", "Cache");
            var runner = new FakeProcessRunner()
                .Responding(poetry, "config cache-dir", cache)
                .Responding(poetry, "config virtualenvs.path", Path.Combine(cache, "virtualenvs"));

            return new Built(
                new PoetryCacheProvider(
                    environment, runner, FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations, volumes: new FakeVolumeInventory()),
                CreateCache(Path.Combine(cache, "artifacts")),
                cache);
        },
        ["Redshift"] = setup =>
        {
            var textures = Path.Combine(setup.Environment.LocalAppData, "Redshift", "Cache", "Textures");
            var bucket = Path.Combine(textures, "107");
            CreateCache(Path.Combine(bucket, "202"));

            return new Built(
                new RedshiftCacheProvider(
                    setup.Environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner,
                    volumes: new FakeVolumeInventory().With(Path.GetDirectoryName(setup.Environment.UserProfile)!),
                    declarations: setup.Declarations),
                bucket,
                textures);
        },
        ["Claude Code leftovers"] = setup =>
        {
            var claude = new ClaudeCodeFixture(setup.Environment);

            return new Built(
                new ClaudeCodeDerivedStateProvider(
                    setup.Environment, runner: new FakeProcessRunner(), inspector: FakeProcessInspector.NothingRunning,
                    scanner: setup.Scanner, declarations: setup.Declarations),
                claude.SpilledOutput(ClaudeCodeFixture.SessionA, age: ClaudeCodeFixture.Old),
                claude.Home);
        },
    };

    /// <summary>One repository's cache, holding only what restic's own cleanup accepts.</summary>
    private static string CreateResticRepository(FakeUserEnvironment environment)
    {
        var repository = Path.Combine(
            environment.LocalAppData, "restic", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        foreach (var name in new[] { "data", "index", "snapshots" })
        {
            CreateCache(Path.Combine(repository, name));
        }

        File.WriteAllText(Path.Combine(repository, "version"), "1");
        return repository;
    }

    private const string LocalState = "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}";

    /// <summary>An editor's user-data folder, which is found by both its Chromium key and its own state database.</summary>
    private static string CreateEditor(FakeUserEnvironment environment)
    {
        var editor = Path.Combine(environment.RoamingAppData, "Code");
        Directory.CreateDirectory(Path.Combine(editor, "User", "globalStorage"));
        File.WriteAllText(Path.Combine(editor, "Local State"), LocalState);
        File.WriteAllBytes(Path.Combine(editor, "User", "globalStorage", "state.vscdb"), new byte[64]);
        return editor;
    }

    public static TheoryData<string> Rows => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task EachRowMeasuresWhileTheDeclarationsAreRead(string row)
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var scanner = new FakeDirectoryScanner();
        var declarations = new RowDeclarations();
        var built = Cases[row](new Setup(environment, scanner, declarations));

        var unrecognised = Path.Combine(built.Parent, Unrecognised);
        var offered = Path.Combine(built.Parent, Offered);
        Directory.CreateDirectory(unrecognised);
        Directory.CreateDirectory(offered);

        using var reading = new ManualResetEventSlim();

        declarations.Admit(
        [
            new DeclaringRow(() =>
            {
                reading.Wait();
                return [ToolRoot.Folders(built.Parent, "Another row's cache.", name => name == Offered)];
            }),
        ]);

        var planning = Task.Run(() => built.Row.PlanAsync());

        try
        {
            await scanner.WhenMeasured(built.Target).WaitAsync(Patience);
        }
        finally
        {
            reading.Set();
        }

        var plan = await planning.WaitAsync(Patience);
        var survivors = plan.ProtectedPaths.Select(p => p.Path).ToList();

        Assert.Contains(plan.Steps, step => step.Reaches.Contains(built.Target, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(unrecognised, survivors, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(offered, survivors, StringComparer.OrdinalIgnoreCase);
    }
}
