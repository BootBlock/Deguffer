using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// LM Studio's runtimes are removed by LM Studio's own command, which marks a runtime and leaves the
/// deleting to LM Studio's next start, and which starts LM Studio when it finds it closed. The rules
/// worth proving are that nothing is asked of a closed LM Studio, that the runtime it uses and the
/// newest of each line survive (§5.6), that a line Deguffer does not recognise is left alone (§5.2),
/// and that a run reports the space as scheduled only where LM Studio marked the runtime.
/// </summary>
public sealed class LmStudioRuntimeProviderTests : IDisposable
{
    private const string Cuda12 = "llama.cpp-win-x86_64-nvidia-cuda12-avx2";

    private const string Vulkan = "llama.cpp-win-x86_64-vulkan-avx2";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeProcessInspector _inspector = new("LM Studio");
    private readonly FakeProcessRunner _runner = new();

    public LmStudioRuntimeProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Combine(_environment.UserProfile, ".lmstudio");

    private string Backends => Path.Combine(Root, "extensions", "backends");

    private LmStudioRuntimeProvider CreateProvider(TimeProvider? time = null) =>
        new(_environment, _runner, _inspector, time: time);

    /// <summary>LM Studio's folder with <c>lms</c>, the shared libraries, a model, and the given runtimes installed.</summary>
    private void Install(params string[] runtimes)
    {
        Directory.CreateDirectory(Path.Combine(Root, "bin"));
        File.WriteAllBytes(Path.Combine(Root, "bin", "lms.exe"), [0]);

        Directory.CreateDirectory(Path.Combine(Root, "models", "publisher", "model"));
        File.WriteAllBytes(Path.Combine(Root, "models", "publisher", "model", "model.gguf"), new byte[4096]);
        Directory.CreateDirectory(Path.Combine(Root, ".internal"));
        File.WriteAllText(Path.Combine(Root, ".internal", "backend-preferences-v1.json"), "[]");

        Directory.CreateDirectory(Path.Combine(Backends, "vendor", "win-llama-cuda12-vendor-v2"));
        File.WriteAllBytes(Path.Combine(Backends, "vendor", "win-llama-cuda12-vendor-v2", "cudart64_12.dll"), new byte[4096]);

        foreach (var runtime in runtimes)
        {
            var folder = Folder(runtime);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "llama.dll"), new byte[8192]);
        }
    }

    /// <summary>Where LM Studio keeps a runtime given as <c>name@version</c>.</summary>
    private string Folder(string runtime) => Path.Combine(Backends, runtime.Replace('@', '-'));

    /// <summary>What <c>lms runtime ls</c> prints for these runtimes, the selected one marked.</summary>
    private FakeProcessRunner Listing(string selected, params string[] runtimes)
    {
        var rows = string.Concat(runtimes.Select(runtime =>
            $"{runtime,-50}{(runtime == selected ? "   ✓    " : "        ")}    GGUF    \r\n"));

        return _runner.Responding(
            "runtime ls",
            "LLM ENGINE                                        SELECTED    MODEL FORMAT\r\n" + rows);
    }

    /// <summary>LM Studio answering a remove as it does on Windows: by marking the folder, and nothing else.</summary>
    private FakeProcessRunner MarkingOnRemove() => _runner.Replying(arguments =>
    {
        if (!arguments.StartsWith("runtime remove --yes ", StringComparison.Ordinal))
        {
            return null;
        }

        var runtime = arguments["runtime remove --yes ".Length..];
        File.WriteAllText(
            Path.Combine(Folder(runtime), LmStudioRuntimeProvider.Marker),
            "This backend is marked for deletion on 01/06/2026, 12:00:00.");

        return new CommandOutcome(0, $"About to remove {runtime}\nRemoved {runtime}\n", string.Empty);
    });

    [Fact]
    public async Task IsNotPresentWithoutRuntimes()
    {
        Assert.False(await CreateProvider().IsPresentAsync());
        Assert.True((await CreateProvider().PlanAsync()).IsEmpty);
    }

    /// <summary>One version of each line is a working install with nothing to reclaim.</summary>
    [Fact]
    public async Task IsNotPresentWithOneVersionOfEachLine()
    {
        Install($"{Cuda12}@2.46.0", $"{Vulkan}@2.13.0");

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task IsPresentWithTwoVersionsOfALine()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>A version LM Studio has already marked goes at its next start, so it is not a second version.</summary>
    [Fact]
    public async Task AVersionAlreadyMarkedDoesNotMakeItPresent()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.41.0");
        File.WriteAllText(Path.Combine(Folder($"{Cuda12}@2.41.0"), LmStudioRuntimeProvider.Marker), "marked");

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// <c>lms</c> starts LM Studio when it finds it closed, so with LM Studio closed nothing is asked of
    /// it, and the row does not claim to be clear about runtimes nobody listed.
    /// </summary>
    [Fact]
    public async Task AsksNothingWhileLmStudioIsClosed()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _inspector.WithoutRunning("LM Studio");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(_runner.Invocations);
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.StartsWith("Open LM Studio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AsksTheServiceWhenOnlyTheServiceIsRunning()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _inspector.WithoutRunning("LM Studio").WithRunning("llmster");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task OffersEveryVersionButTheSelectedAndTheNewestOfEachLine()
    {
        string[] runtimes =
        [
            $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0",
            $"{Vulkan}@2.28.2", $"{Vulkan}@2.13.0",
        ];
        Install(runtimes);
        Listing($"{Cuda12}@2.45.0", runtimes);

        var plan = await CreateProvider().PlanAsync();

        var steps = plan.Steps.Cast<RunCommandStep>().ToList();

        Assert.Equal(
            [$"runtime remove --yes {Cuda12}@2.44.0", $"runtime remove --yes {Vulkan}@2.13.0"],
            steps.Select(step => step.Arguments));
        Assert.All(steps, step => Assert.Equal(Path.Combine(Root, "bin", "lms.exe"), step.FileName));

        var older = steps[0];
        Assert.Equal(Folder($"{Cuda12}@2.44.0"), older.Removes);
        Assert.Equal([Folder($"{Cuda12}@2.44.0")], older.MeasuredPaths);
        Assert.Equal(8192, older.EstimatedBytes);
        Assert.Equal(LmStudioRuntimeProvider.Marker, older.Scheduled?.Marker);
        Assert.Contains("LM Studio", older.RunsOnlyWhile);
        Assert.Equal(new ItemIdentity($"{Cuda12}@2.44.0", $"{Cuda12} 2.44.0"), older.Identity);
        Assert.Equal([new ItemFacet("Version", "2.44.0")], older.Facets);
        Assert.Equal(Cuda12, older.Group);

        // §5.6: the selected runtime, which is not the newest here, and the newest, which is not
        // selected, are both asserted to survive, beside the folders around them.
        foreach (var survivor in new[]
                 {
                     Folder($"{Cuda12}@2.46.0"), Folder($"{Cuda12}@2.45.0"), Folder($"{Vulkan}@2.28.2"),
                     Root, Backends, Path.Combine(Backends, "vendor"), Path.Combine(Root, "models"), Path.Combine(Root, ".internal"),
                 })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            Assert.DoesNotContain(plan.Steps, step => step.Subjects.Contains(survivor, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Without the version, <c>lms runtime remove</c> matches every version of the line, the one in use
    /// included.
    /// </summary>
    [Fact]
    public async Task NamesTheVersionInEveryCommand()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.All(plan.Steps.Cast<RunCommandStep>(), step => Assert.Matches(@"@[0-9.]+\z", step.Arguments));
    }

    [Fact]
    public async Task OffersNothingWhenLmStudioSelectsNothing()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing(selected: "", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    [Theory]
    [InlineData(0, "Failed to start or connect to local LM Studio API server.")]
    [InlineData(1, "")]
    public async Task OffersNothingWhenTheListingCannotBeRead(int exitCode, string output)
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _runner.Responding("runtime ls", output, exitCode);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>§5.2: a line is recognised as a llama.cpp build for Windows with a plain version, or not at all.</summary>
    [Theory]
    [InlineData("mlx-llm-win-x86_64", "0.9.0", "0.8.0")]
    [InlineData("llama.cpp-win-x86_64-avx2", "2.46.0", "2.45.0-beta")]
    public async Task LeavesALineItDoesNotRecogniseAlone(string line, string newer, string older)
    {
        Install($"{Cuda12}@2.46.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{line}@{newer}", $"{line}@{older}");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains(line, StringComparison.Ordinal)
            && note.Message.Contains("not a runtime Deguffer recognises", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavesARuntimeWhoseFolderIsNotWhereLmStudioKeepsThemAlone()
    {
        Install($"{Cuda12}@2.46.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"{Cuda12}@2.45.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavesARuntimeLmStudioHasAlreadyMarkedAlone()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        File.WriteAllText(Path.Combine(Folder($"{Cuda12}@2.45.0"), LmStudioRuntimeProvider.Marker), "marked");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("already marked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavesARuntimeWhoseFolderIsALinkAlone()
    {
        Install($"{Cuda12}@2.46.0");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "llama.dll"), new byte[8192]);
        SymbolicLink.ToDirectory(Folder($"{Cuda12}@2.45.0"), outside);
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("link to somewhere else", StringComparison.Ordinal));
    }

    /// <summary>
    /// The run frees nothing it can measure, because LM Studio deletes at its next start. So the
    /// figure is reported as scheduled, and only because the marker is there.
    /// </summary>
    [Fact]
    public async Task ReportsTheSpaceAsScheduledOnceLmStudioMarksTheRuntime()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        MarkingOnRemove();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Equal(8192, outcome.BytesScheduled);
        Assert.Contains("next time it starts", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(_runner.Invocations, call => call.Arguments == $"runtime remove --yes {Cuda12}@2.45.0");

        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.True(Directory.Exists(Folder($"{Cuda12}@2.46.0")));
    }

    /// <summary>
    /// <c>lms</c> reports success before LM Studio has written anything, and a failure after that never
    /// reaches it, so a runtime left unmarked is a step that did nothing.
    /// </summary>
    [Fact]
    public async Task FailsTheStepWhenLmStudioNeverMarksTheRuntime()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _runner.Responding("runtime remove", $"Removed {Cuda12}@2.45.0\n");

        var clock = new ManualTimeProvider();
        var provider = CreateProvider(clock);
        var plan = await provider.PlanAsync();

        var running = provider.ExecuteAsync(plan);

        // The run waits on the clock between looks for the marker, so the clock is moved on each time
        // it does, and the run gives up once the clock passes its limit.
        var patience = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!running.IsCompleted && DateTime.UtcNow < patience)
        {
            if (clock.Waiting > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            else
            {
                await Task.Delay(10);
            }
        }

        var outcome = Assert.Single((await running.WaitAsync(TimeSpan.FromSeconds(1))).Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesScheduled);
        Assert.Contains("neither removed", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan was made while LM Studio ran, and the user can close it while the preview is on
    /// screen. <c>lms</c> would then start it, so the command does not run.
    /// </summary>
    [Fact]
    public async Task DoesNotRunTheCommandOnceLmStudioIsClosed()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        _inspector.WithoutRunning("LM Studio");
        var asked = _inspector.InvalidateCount;
        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("LM Studio is no longer running", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Invocations, call => call.Arguments.StartsWith("runtime remove", StringComparison.Ordinal));

        // The real inspector answers from a snapshot of the process table, so the run has to discard
        // it to see a program closed since the preview. This fake has no snapshot to go stale.
        Assert.True(_inspector.InvalidateCount > asked);
    }

    /// <summary>A tool that removes the runtime at once after all is reported as having freed it.</summary>
    [Fact]
    public async Task ReportsTheSpaceAsReclaimedWhereTheRuntimeGoesAtOnce()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _runner.Replying(arguments =>
        {
            if (!arguments.StartsWith("runtime remove", StringComparison.Ordinal))
            {
                return null;
            }

            Directory.Delete(Folder($"{Cuda12}@2.45.0"), recursive: true);
            return new CommandOutcome(0, $"Removed {Cuda12}@2.45.0\n", string.Empty);
        });

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var outcome = Assert.Single(result.Steps);
        Assert.True(outcome.Succeeded);
        Assert.Equal(8192, outcome.BytesReclaimed);
        Assert.Equal(0, outcome.BytesScheduled);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
    }

    /// <summary>
    /// §5.6 where the removal is deferred. A command that reached the runtime LM Studio uses would
    /// mark it and leave it standing, and LM Studio would delete it at its next start, so the run
    /// fails on the marker rather than passing on the folder.
    /// </summary>
    [Fact]
    public async Task FailsTheRunWhenAKeptRuntimeIsMarked()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0");
        _runner.Replying(arguments =>
        {
            if (!arguments.StartsWith("runtime remove", StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var runtime in new[] { $"{Cuda12}@2.45.0", $"{Cuda12}@2.46.0" })
            {
                File.WriteAllText(Path.Combine(Folder(runtime), LmStudioRuntimeProvider.Marker), "marked");
            }

            return new CommandOutcome(0, $"Removed {Cuda12}@2.45.0\n", string.Empty);
        });

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.NotNull(result.Verification);
        Assert.False(result.Verification.Passed);
        Assert.Contains(result.Verification.Failures, check =>
            check.Subject == Folder($"{Cuda12}@2.46.0") && check.Detail.StartsWith("MARKED", StringComparison.Ordinal));
    }

    /// <summary>The same for a runtime the user declined, which sits beside the one removed.</summary>
    [Fact]
    public async Task FailsTheRunWhenADeclinedRuntimeIsMarked()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");
        _runner.Replying(arguments =>
        {
            if (!arguments.StartsWith("runtime remove", StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var runtime in new[] { $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0" })
            {
                File.WriteAllText(Path.Combine(Folder(runtime), LmStudioRuntimeProvider.Marker), "marked");
            }

            return new CommandOutcome(0, $"Removed {Cuda12}@2.44.0\n", string.Empty);
        });

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var chosen = plan.Steps.Single(step => step.Subjects.Contains(Folder($"{Cuda12}@2.44.0")));

        var result = await provider.ExecuteAsync(plan.NarrowedTo([chosen]));

        Assert.NotNull(result.Verification);
        Assert.Contains(result.Verification.Failures, check =>
            check.Subject == Folder($"{Cuda12}@2.45.0") && check.Detail.StartsWith("MARKED", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2 and §5.6: a folder beside the runtimes that Deguffer does not recognise, or that LM Studio
    /// did not list, is proved standing and unmarked. One LM Studio had marked already is not
    /// protected, because LM Studio deletes it whenever it next starts, which may be before the clean.
    /// </summary>
    [Fact]
    public async Task ProtectsEveryFolderBesideTheRuntimesOffered()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.41.0");
        File.WriteAllText(Path.Combine(Folder($"{Cuda12}@2.41.0"), LmStudioRuntimeProvider.Marker), "marked");
        var stranger = Path.Combine(Backends, "mlx-llm-win-x86_64-0.9.0");
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "engine.dll"), new byte[4096]);
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", "mlx-llm-win-x86_64@0.9.0");
        MarkingOnRemove();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([$"runtime remove --yes {Cuda12}@2.45.0"], plan.Steps.Cast<RunCommandStep>().Select(step => step.Arguments));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == stranger && p.Marker == LmStudioRuntimeProvider.Marker);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.46.0") && p.Marker == LmStudioRuntimeProvider.Marker);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.41.0"));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.45.0"));

        // LM Studio starting between the preview and the clean deletes what it had marked.
        Directory.Delete(Folder($"{Cuda12}@2.41.0"), recursive: true);

        var result = await provider.ExecuteAsync(plan);

        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.True(File.Exists(Path.Combine(stranger, "engine.dll")));
    }

    /// <summary>
    /// Two versions that are one version as numbers are both the newest, whichever way they are
    /// written, so neither is offered.
    /// </summary>
    [Fact]
    public async Task KeepsEveryVersionEqualToTheNewest()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.046.0", $"{Cuda12}@2.45.0");
        Listing($"{Cuda12}@2.45.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.046.0", $"{Cuda12}@2.45.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>A version too long for a number is not a runtime Deguffer recognises, and it does not fail the preview.</summary>
    [Fact]
    public async Task LeavesALineWithAnOverlongVersionAlone()
    {
        Install($"{Cuda12}@2.46.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", "llama.cpp-win-x86_64-avx2@2.99999999999", "llama.cpp-win-x86_64-avx2@2.1.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("not a runtime Deguffer recognises", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6 for a runtime the user declined: its command never runs, so the run can promise it is
    /// still there, and it sits beside the one removed in the same shape.
    /// </summary>
    [Fact]
    public async Task ProtectsARuntimeTheUserDeclined()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");

        var plan = await CreateProvider().PlanAsync();
        var chosen = plan.Steps.Single(step => step.Subjects.Contains(Folder($"{Cuda12}@2.44.0")));

        var narrowed = plan.NarrowedTo([chosen]);

        Assert.Contains(narrowed.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.45.0"));
        Assert.DoesNotContain(narrowed.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.44.0"));
    }

    /// <summary>A kept runtime is matched by its name and version, and protected where it is.</summary>
    [Fact]
    public async Task KeepsARuntimeOnTheKeepList()
    {
        Install($"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");
        Listing($"{Cuda12}@2.46.0", $"{Cuda12}@2.46.0", $"{Cuda12}@2.45.0", $"{Cuda12}@2.44.0");

        var plan = await CreateProvider().PlanAsync();

        var kept = plan.WithKeepList(new HashSet<string>([$"{Cuda12}@2.45.0"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal([$"runtime remove --yes {Cuda12}@2.44.0"], kept.Steps.Cast<RunCommandStep>().Select(step => step.Arguments));
        Assert.Contains(kept.ProtectedPaths, p => p.Path == Folder($"{Cuda12}@2.45.0") && p.Withheld == Withholding.OnKeepList);
    }

    /// <summary>§7.1: Explore may take nothing from LM Studio's folder or from among its runtimes directly.</summary>
    [Fact]
    public void DeclaresLmStudiosFoldersWithNothingRecognised()
    {
        var provider = CreateProvider();

        Assert.Equal([provider.Root, provider.Backends], provider.ToolRoots.Select(root => root.Path));
        Assert.All(provider.ToolRoots, root =>
        {
            Assert.False(root.Recognises("models"));
            Assert.False(root.Recognises($"{Cuda12}-2.45.0"));
            Assert.False(root.Recognises("vendor"));
        });
    }
}
