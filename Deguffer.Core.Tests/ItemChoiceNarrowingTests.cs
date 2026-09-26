using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The list of items is a new way to choose what a run takes, and choosing must still go through
/// <see cref="CleanupPlan.NarrowedTo"/> and nothing else. The case that matters is a sibling hidden by
/// a search inside the very group whose heading was ticked: same parent, same shape, and not on
/// screen, which is exactly when an over-broad choice takes both.
/// </summary>
public sealed class ItemChoiceNarrowingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ItemChoiceNarrowingTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TickingAGroupWhileSearchingTakesOnlyWhatWasShownAndProtectsTheRest()
    {
        var root = Path.Combine(_environment.LocalAppData, "ms-playwright");
        string[] builds = ["chromium-1228", "chromium-1300", "firefox-1532", "webkit-2210"];

        foreach (var build in builds)
        {
            Directory.CreateDirectory(Path.Combine(root, build));
            File.WriteAllBytes(Path.Combine(root, build, "payload.bin"), new byte[4096]);
        }

        var provider = new PlaywrightBrowsersProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        var plan = await provider.PlanAsync();

        // What the list does: group, search, then click the heading of what is left on screen. The whole
        // build name is searched rather than its revision, because every description ends in a path under
        // a scratch folder whose random name can hold any four digits, and would then match every build.
        var shown = new ItemFilter("chromium-1300").Showing(ItemGroups.Of(plan.Steps, step => step), step => step);
        var chromium = Assert.Single(shown);
        Assert.Equal("chromium", chromium.Name);

        // Tier 2 starts unticked, so the heading is clear, and the click writes its value to every build
        // under it that can be ticked. The run is built from what the click wrote.
        var state = ItemSelection.StateOf(chromium.Items.Select(step => (false, step.RemovesSomething)));
        var written = ItemSelection.ValueForClick(state);

        var narrowed = plan.NarrowedTo([.. chromium.Items.Where(step => written && step.RemovesSomething)]);

        var taken = Path.Combine(root, "chromium-1300");
        Assert.Equal([taken], narrowed.TargetedPaths);

        foreach (var build in builds.Where(b => b != "chromium-1300"))
        {
            var path = Path.Combine(root, build);
            var protection = Assert.Single(
                narrowed.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(PathPresence.Present, protection.PresenceBefore);
        }

        var result = await provider.ExecuteAsync(narrowed);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(taken));

        foreach (var build in builds.Where(b => b != "chromium-1300"))
        {
            Assert.True(
                Directory.Exists(Path.Combine(root, build)),
                $"{build} was not ticked, or was hidden by the search, and it was deleted anyway.");
        }

        Assert.True(Directory.Exists(root));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
