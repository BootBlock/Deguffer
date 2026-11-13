using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Hugging Face hub cache is removed one repository at a time by <c>hf cache rm</c>, and only by a
/// version that knows about the shared blob store. The rules worth proving are that nothing is offered
/// below 1.32, that nothing is ever removed by path, that everything beside the repositories offered
/// survives (§5.6), the access tokens and the shared store first, that a folder the tool did not list is
/// left alone (§5.2), and that a repository a program has open is held back at the preview and at the
/// clean.
/// </summary>
public sealed class HuggingFaceHubProviderTests : IDisposable
{
    private const string Gpt2 = "model/openai-community/gpt2";

    private const string Whisper = "model/Systran/faster-whisper-small";

    private const string Squad = "dataset/rajpurkar/squad";

    private const string Revision = "607a30d783dfa663caf39e06633721c8d4cfcd7e";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;
    private readonly Dictionary<string, long> _listed = new(StringComparer.Ordinal);

    public HuggingFaceHubProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("hf", Hf);
        Version = "2.4.0";
        _runner.Replying(Hf, Reply);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The <c>hf</c> the fake answers for: it answers no other program.</summary>
    private string Hf => Path.Combine(_temp.Path, "Python", "Scripts", "hf.exe");

    private string Home => Path.Combine(_environment.UserProfile, ".cache", "huggingface");

    private string Hub => Path.Combine(Home, "hub");

    /// <summary>The version <c>hf env</c> reports.</summary>
    private string Version { get; set; }

    /// <summary>What <c>hf env</c> reports as the hub cache, where a test moves it.</summary>
    private string? ReportedHub { get; set; }

    /// <summary>What <c>hf cache rm</c> does to the cache, where a test wants more than its removal.</summary>
    private Action<string>? OnRemove { get; set; }

    private HuggingFaceHubProvider CreateProvider() => new(_environment, _runner, liveTrees: _liveTrees);

    private string Folder(string id)
    {
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return Path.Combine(Hub, $"{id[..slash]}s--{id[(slash + 1)..].Replace("/", "--", StringComparison.Ordinal)}");
    }

    private string SnapshotFile(string id) => Path.Combine(Folder(id), "snapshots", Revision, "model.safetensors");

    /// <summary>
    /// A hub cache as the tool leaves it, with the tool's own files, the shared store, the Hugging Face
    /// folder's tokens and assets, and each repository with one revision.
    /// </summary>
    private void Install(params (string Id, long Bytes)[] repositories)
    {
        Directory.CreateDirectory(Path.Combine(Hub, ".locks"));
        File.WriteAllText(Path.Combine(Hub, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55");
        Directory.CreateDirectory(Path.Combine(Hub, "blobs", "f5"));
        File.WriteAllText(Path.Combine(Hub, "blobs", ".huggingface-shared-blobs"), "1\n");
        File.WriteAllBytes(Path.Combine(Hub, "blobs", "f5", new string('f', 64)), new byte[4096]);

        File.WriteAllText(Path.Combine(Home, "token"), "<YOUR_API_KEY>");
        File.WriteAllText(Path.Combine(Home, "stored_tokens"), "[default]");
        Directory.CreateDirectory(Path.Combine(Home, "assets"));
        Directory.CreateDirectory(Path.Combine(Home, "xet"));

        foreach (var (id, bytes) in repositories)
        {
            var folder = Folder(id);
            Directory.CreateDirectory(Path.Combine(folder, "blobs"));
            Directory.CreateDirectory(Path.Combine(folder, "refs"));
            Directory.CreateDirectory(Path.Combine(folder, "snapshots", Revision));
            File.WriteAllText(Path.Combine(folder, "refs", "main"), Revision);
            File.WriteAllBytes(Path.Combine(folder, "blobs", "0473a084265e924a69db9c05daaea027c7129156"), new byte[8192]);
            File.WriteAllBytes(SnapshotFile(id), new byte[8192]);
            _listed[id] = bytes;
        }
    }

    /// <summary>
    /// <c>hf</c> as it answers on Windows: <c>env</c> with the folder it joined in forward slashes and
    /// the part it added with a backslash, <c>cache ls</c> with every repository still listed, and
    /// <c>cache rm</c> by removing the repository's folder.
    /// </summary>
    private CommandOutcome? Reply(string arguments)
    {
        if (arguments == HuggingFaceCommands.Environment)
        {
            var home = Home.Replace('\\', '/');
            return new CommandOutcome(
                0,
                "\nCopy-and-paste the text below in your GitHub issue.\n\n"
                + $"- huggingface_hub version: {Version}\n"
                + "- Platform: Windows-10-10.0.26100-SP0\n"
                + $"- Token path ?: {home}\\token\n"
                + $"- HF_HUB_CACHE: {ReportedHub ?? $"{home}\\hub"}\n"
                + $"- HF_ASSETS_CACHE: {home}\\assets\n"
                + $"- HF_TOKEN_PATH: {home}\\token\n"
                + $"- HF_STORED_TOKENS_PATH: {home}\\stored_tokens\n",
                string.Empty);
        }

        if (arguments.StartsWith("cache ls ", StringComparison.Ordinal))
        {
            var entries = _listed.Select(item =>
            {
                var slash = item.Key.IndexOf('/', StringComparison.Ordinal);
                return $"{{\"id\": \"{item.Key}\", \"repo_id\": \"{item.Key[(slash + 1)..]}\", \"repo_type\": \"{item.Key[..slash]}\", "
                    + FormattableString.Invariant($"\"size\": \"{item.Value / 1_000_000.0:0.0}M\", \"last_accessed\": \"a few seconds ago\", ")
                    + "\"last_modified\": \"a few seconds ago\", \"refs\": [\"main\"]}";
            });

            return new CommandOutcome(0, $"[{string.Join(", ", entries)}]\n", string.Empty);
        }

        if (arguments.StartsWith("cache rm ", StringComparison.Ordinal))
        {
            var id = arguments.Split(' ')[2];
            Directory.Delete(Folder(id), recursive: true);
            _listed.Remove(id);
            OnRemove?.Invoke(id);
            return new CommandOutcome(0, "{\"repos_deleted\": 1, \"revisions_deleted\": 1, \"freed\": \"3.1G\"}\n", string.Empty);
        }

        return null;
    }

    [Fact]
    public async Task IsNotPresentWithoutTheTool()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        Install((Gpt2, 548_000_000));

        Assert.False(await new HuggingFaceHubProvider(environment, _runner, liveTrees: _liveTrees).IsPresentAsync());
    }

    /// <summary>
    /// #450: the tool is asked its version and where its cache is, then to list the cache, as the pass
    /// begins, and the plan awaits both answers.
    /// </summary>
    [Fact]
    public async Task AsksTheToolItsEnvironmentAndItsListingAsThePassBegins()
    {
        Install((Gpt2, 548_000_000));

        var plan = await EarlyStartProof.StartsBeforeItPlansAsync(CreateProvider(), _runner, HuggingFaceCommands.Environment, "cache ls ");

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task StartsNothingAsThePassBeginsWhenTheToolIsNotInstalled()
    {
        var runner = new FakeProcessRunner();

        await EarlyStartProof.StartsNothingAsync(
            new HuggingFaceHubProvider(new FakeUserEnvironment(_temp.Path), runner, liveTrees: _liveTrees), runner);
    }

    /// <summary>The listing waits, as in the plan, on a version that removes a repository's shared files.</summary>
    [Fact]
    public async Task StopsAfterItsEnvironmentAsThePassBeginsWhenTheToolIsTooOld()
    {
        Version = "1.31.0";
        Install((Gpt2, 548_000_000));

        await EarlyStartProof.StartsOnlyAsync(CreateProvider(), _runner, HuggingFaceCommands.Environment);
    }

    [Fact]
    public async Task IsNotPresentWhereTheToolHasDownloadedNothing()
    {
        Assert.False(await CreateProvider().IsPresentAsync());
        Assert.True((await CreateProvider().PlanAsync()).IsEmpty);
    }

    /// <summary>A version too old to be asked still shows the row, because the plan says why it offers nothing.</summary>
    [Fact]
    public async Task IsPresentWithACacheAndATooOldTool()
    {
        Version = "1.31.0";
        Install((Gpt2, 548_000_000));

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// Below 1.32 the tool leaves a removed repository's files in the shared store, so nothing is offered
    /// and the plan says to update. 1.32 itself, and a release candidate of it, offer.
    /// </summary>
    [Theory]
    [InlineData("1.31.0", false)]
    [InlineData("0.36.0", false)]
    [InlineData("1.32.0", true)]
    [InlineData("1.32.0rc0", true)]
    [InlineData("2.4.0", true)]
    public async Task OffersNothingBelowTheVersionThatKnowsTheSharedStore(string version, bool offered)
    {
        Version = version;
        Install((Gpt2, 548_000_000));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(offered, plan.Steps.Count == 1);

        if (!offered)
        {
            Assert.Contains(plan.Notes, note => note.Message.Contains("pip install -U huggingface_hub", StringComparison.Ordinal));
            Assert.DoesNotContain(_runner.Invocations, invocation => invocation.Arguments.StartsWith("cache ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task OffersEachRepositoryThroughTheToolsOwnRemove()
    {
        Install((Gpt2, 3_100_000_000), (Whisper, 484_000_000), (Squad, 89_500_000));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.Empty(plan.TargetedPaths);

        var steps = plan.Steps.Cast<RunCommandStep>().ToDictionary(step => step.Identity!.Key);
        Assert.Equal(new[] { Gpt2, Squad, Whisper }.Order(StringComparer.Ordinal), steps.Keys.Order(StringComparer.Ordinal));

        var gpt2 = steps[Gpt2];
        Assert.Equal(Hf, gpt2.FileName);
        Assert.Equal($"cache rm {Gpt2} --cache-dir \"{Hub}\" --format json --yes", gpt2.Arguments);
        Assert.Equal(Folder(Gpt2), gpt2.Removes);
        Assert.Equal([Folder(Gpt2)], gpt2.MeasuredPaths);
        Assert.Equal(3_100_000_000, gpt2.EstimatedBytes);
        Assert.True(gpt2.Estimated.IsApproximate);
        Assert.Equal(CancelledCommand.Finishes, gpt2.WhenCancelled);
        Assert.NotNull(gpt2.MeasuredBy);
        Assert.NotNull(gpt2.TargetCheck);
        Assert.Equal(new ItemIdentity(Gpt2, "openai-community/gpt2"), gpt2.Identity);
        Assert.Equal("Models", gpt2.Group);
        Assert.Equal("Datasets", steps[Squad].Group);

        // The listing is told the same folder the removal is, so what was examined is what is acted on.
        Assert.Contains(_runner.Invocations, invocation => invocation.Arguments == $"cache ls --format json --cache-dir \"{Hub}\"");
    }

    /// <summary>
    /// §6.3 by the form of the path: the tool is told the folder as the user would write it, and the
    /// files asked about for use are named in that form too, though they are listed in long form.
    /// </summary>
    [Fact]
    public async Task NamesEveryPathInDisplayForm()
    {
        Install((Gpt2, 548_000_000));

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.Cast<RunCommandStep>());
        Assert.DoesNotContain(@"\\?\", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\?\", step.Removes!, StringComparison.Ordinal);
        var asked = Assert.Single(_liveTrees.Asked);
        Assert.Equal([SnapshotFile(Gpt2)], asked.LockFileNames);
        Assert.All(plan.ProtectedPaths, path => Assert.DoesNotContain(@"\\?\", path.Path, StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2 and §5.6: the tool's own files, the shared store, a folder the tool did not list, the
    /// tokens and every sibling of the hub cache are asserted standing, and none is a step's subject.
    /// </summary>
    [Fact]
    public async Task ProtectsEverythingBesideTheRepositoriesOffered()
    {
        Install((Gpt2, 548_000_000));
        var unlisted = Path.Combine(Hub, "models--hf-internal-testing--tiny-random-BertModel");
        Directory.CreateDirectory(Path.Combine(unlisted, "blobs"));
        File.WriteAllBytes(Path.Combine(unlisted, "blobs", "partial.incomplete"), new byte[4096]);

        var plan = await CreateProvider().PlanAsync();

        foreach (var survivor in new[]
                 {
                     Hub, Path.Combine(Hub, "CACHEDIR.TAG"), Path.Combine(Hub, ".locks"), Path.Combine(Hub, "blobs"), unlisted,
                     Path.Combine(Home, "token"), Path.Combine(Home, "stored_tokens"), Path.Combine(Home, "assets"), Path.Combine(Home, "xet"),
                 })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            Assert.DoesNotContain(plan.Steps, step => step.Subjects.Contains(survivor, StringComparer.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(Folder(Gpt2), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The run passes when the tool removes only the repository chosen, and reports the tool's figure
    /// as freed, asked again at the clean: a revision downloaded after the preview goes with the
    /// repository, and the plan's figure would leave it out.
    /// </summary>
    [Fact]
    public async Task ReportsTheToolsFigureAsFreed()
    {
        Install((Gpt2, 548_000_000));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _listed[Gpt2] = 612_000_000;

        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.True(outcome.Succeeded);
        Assert.Equal(612_000_000, outcome.BytesReclaimed);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.False(Directory.Exists(Folder(Gpt2)));
        Assert.True(File.Exists(Path.Combine(Home, "token")));
    }

    /// <summary>
    /// §5.6 bites: a removal that also took the shared store, or the folder the tool did not list, fails
    /// the run, though every positive assertion about the repository chosen holds.
    /// </summary>
    [Theory]
    [InlineData("blobs")]
    [InlineData("models--hf-internal-testing--tiny-random-BertModel")]
    public async Task FailsTheRunWhenTheRemovalReachesBeyondTheRepository(string beside)
    {
        Install((Gpt2, 548_000_000));
        Directory.CreateDirectory(Path.Combine(Hub, "models--hf-internal-testing--tiny-random-BertModel", "blobs"));
        OnRemove = _ => Directory.Delete(Path.Combine(Hub, beside), recursive: true);

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.NotNull(result.Verification);
        Assert.False(result.Verification.Passed);
        Assert.Contains(result.Verification.Failures, check => check.Subject.Equals(Path.Combine(Hub, beside), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A repository the user declined is protected, and it sits beside the one removed in the same shape.</summary>
    [Fact]
    public async Task ProtectsARepositoryTheUserDeclined()
    {
        Install((Gpt2, 548_000_000), (Whisper, 484_000_000));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var chosen = plan.Steps.Single(step => step.Subjects.Contains(Folder(Gpt2)));
        var narrowed = await plan.NarrowedToAsync([chosen]);

        Assert.Contains(narrowed.ProtectedPaths, p => p.Path == Folder(Whisper));

        OnRemove = _ => Directory.Delete(Folder(Whisper), recursive: true);
        var result = await provider.ExecuteAsync(narrowed);

        Assert.NotNull(result.Verification);
        Assert.Contains(result.Verification.Failures, check => check.Subject == Folder(Whisper));
    }

    [Fact]
    public async Task KeepsARepositoryOnTheKeepList()
    {
        Install((Gpt2, 548_000_000), (Whisper, 484_000_000));

        var plan = await CreateProvider().PlanAsync();
        var kept = plan.WithKeepList(new HashSet<string>([Whisper], StringComparer.OrdinalIgnoreCase));

        Assert.Equal([Folder(Gpt2)], kept.Steps.Select(step => step.Subjects.Single()));
        Assert.Contains(kept.ProtectedPaths, p => p.Path == Folder(Whisper) && p.Withheld == Withholding.OnKeepList);
    }

    /// <summary>A model a program has loaded holds its weights open, so its repository is not offered and is asserted to survive.</summary>
    [Fact]
    public async Task HoldsBackARepositoryAProgramHasOpen()
    {
        Install((Gpt2, 548_000_000), (Whisper, 484_000_000));
        _liveTrees.WithHeldFile(SnapshotFile(Whisper), "python.exe");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Folder(Gpt2)], plan.Steps.Select(step => step.Subjects.Single()));
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning && note.Message.Contains(Whisper, StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Folder(Whisper) && p.Reason == HuggingFaceRepositoryUse.InUseReason);
    }

    [Fact]
    public async Task OffersNothingWhereItCannotTellWhetherARepositoryIsInUse()
    {
        Install((Gpt2, 548_000_000));
        _liveTrees.CannotTellFromNow();

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Folder(Gpt2));
    }

    /// <summary>
    /// A revision folder Windows will not describe may hold the file a program has open, so the files
    /// asked about are not all of them, and the repository is held back rather than cleared on the
    /// process table alone.
    /// </summary>
    [Fact]
    public async Task HoldsBackARepositoryWhoseRevisionsWindowsWillNotDescribe()
    {
        Install((Gpt2, 548_000_000));

        using var denied = DeniedDirectory.WithUnreadableAttributes(Path.Combine(Folder(Gpt2), "snapshots"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// A model loaded while the preview is on screen would make the removal stop part-way on the file
    /// it holds, so the run asks again and does not run the command.
    /// </summary>
    [Fact]
    public async Task DoesNotRunTheRemovalOnceAProgramHasTheRepositoryOpen()
    {
        Install((Gpt2, 548_000_000));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _liveTrees.WithHeldFile(SnapshotFile(Gpt2), "python.exe");

        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("python.exe", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Invocations, invocation => invocation.Arguments.StartsWith("cache rm ", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Folder(Gpt2)));
    }

    [Theory]
    [InlineData(0, "Usage: hf cache ls [OPTIONS]")]
    [InlineData(1, "[]")]
    public async Task OffersNothingWhereTheListingCannotBeRead(int exitCode, string output)
    {
        Install((Gpt2, 548_000_000));
        _runner.Replying(Hf, arguments => arguments.StartsWith("cache ls ", StringComparison.Ordinal)
            ? new CommandOutcome(exitCode, output, string.Empty)
            : Reply(arguments));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>A repository the tool lists in a folder Deguffer finds to be a link is left alone, and asserted to survive.</summary>
    [Fact]
    public async Task LeavesARepositoryWhoseFolderIsALinkAlone()
    {
        Install();
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "snapshots"));
        SymbolicLink.ToDirectory(Folder(Gpt2), outside);
        _listed[Gpt2] = 548_000_000;

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Folder(Gpt2) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task LeavesARepositoryListedOutsideTheFolderTheToolKeepsItInAlone()
    {
        Install();
        _listed[Gpt2] = 548_000_000;

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"Leaving {Gpt2} alone", StringComparison.Ordinal));
    }

    /// <summary>A hub cache moved onto the profile itself is not examined as Hugging Face's, and the command is never sent there.</summary>
    [Fact]
    public async Task DeclinesAHubCacheSetToTheProfile()
    {
        Install((Gpt2, 548_000_000));
        ReportedHub = _environment.UserProfile;

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("will not run 'hf cache rm' there", StringComparison.Ordinal));
        Assert.DoesNotContain(_runner.Invocations, invocation => invocation.Arguments.StartsWith("cache ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryRepositoryThePlanRemoves()
    {
        Install((Gpt2, 548_000_000), (Squad, 89_500_000));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>§7.1: Explore may take nothing from the hub cache, or from the folder holding the tokens beside it.</summary>
    [Fact]
    public void DeclaresTheHuggingFaceFoldersWithNothingRecognised()
    {
        var provider = CreateProvider();

        Assert.Equal([Home, Hub], provider.ToolRoots.Select(root => root.Path));
        Assert.All(provider.ToolRoots, root =>
        {
            Assert.False(root.RecognisesFolder("models--openai-community--gpt2"));
            Assert.False(root.RecognisesFolder("blobs"));
            Assert.False(root.RecognisesFolder("hub"));
        });
    }

    /// <summary>The variables move what is declared in the order <c>constants.py</c> reads them.</summary>
    [Fact]
    public void DeclaresTheFoldersTheVariablesName()
    {
        var home = Path.Combine(_temp.Path, "hf-home");
        var hub = Path.Combine(_temp.Path, "models");
        _environment
            .WithEnvironmentVariable(HuggingFaceLocations.HomeVariable, home)
            .WithEnvironmentVariable(HuggingFaceLocations.LegacyHubCacheVariable, Path.Combine(_temp.Path, "legacy"))
            .WithEnvironmentVariable(HuggingFaceLocations.HubCacheVariable, hub);

        Assert.Equal([home, hub], CreateProvider().ToolRoots.Select(root => root.Path));
    }

    [Fact]
    public void DeclaresTheHubCacheInsideTheCacheHome()
    {
        var xdg = Path.Combine(_temp.Path, "xdg");
        _environment.WithEnvironmentVariable(HuggingFaceLocations.XdgCacheVariable, xdg);

        Assert.Equal(
            [Path.Combine(xdg, "huggingface"), Path.Combine(xdg, "huggingface", "hub")],
            CreateProvider().ToolRoots.Select(root => root.Path));
    }
}
