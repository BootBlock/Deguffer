using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Azure Functions tooling keeps its own record of what it has downloaded directly beside the
/// downloads. So the rules worth proving are §5.2's — that a release is recognised only when its
/// whole name is a version, and that everything else keeps Tier 4 — and §5.6's, that the feed and
/// the tag records the tooling reads survive a reclaim that empties the folder next to them.
/// </summary>
public sealed class AzureFunctionsToolsProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public AzureFunctionsToolsProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private AzureFunctionsToolsProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Root => Path.Combine(_environment.LocalAppData, AzureFunctionsToolsProvider.RootName);

    private string Releases => Path.Combine(Root, AzureFunctionsToolsProvider.ReleasesName);

    /// <summary>Create the tooling's folder with the given release children, each holding a payload.</summary>
    private string CreateReleases(params string[] children)
    {
        Directory.CreateDirectory(Releases);

        foreach (var child in children)
        {
            var path = Path.Combine(Releases, child);
            Directory.CreateDirectory(Path.Combine(path, "cli_x64"));
            File.WriteAllBytes(Path.Combine(path, "cli_x64", "func.exe"), new byte[4096]);
        }

        return Releases;
    }

    /// <summary>Write the tooling's own record that <paramref name="tag"/> uses <paramref name="version"/>.</summary>
    private void RecordTag(string tag, string version)
    {
        var directory = Path.Combine(Root, AzureFunctionsToolTags.DirectoryName, tag);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "LastKnownGood-v2167102"), version);
    }

    /// <summary>The cached feed, which is what tells the tooling which releases it already holds.</summary>
    private string WriteFeed()
    {
        Directory.CreateDirectory(Root);

        var feed = Path.Combine(Root, "feed-v2167102.json");
        File.WriteAllText(feed, "{}");

        return feed;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheToolingNeverDownloadedARelease()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The folder exists as soon as the tooling has read its feed once, which is before it has
    /// downloaded anything. That is present with nothing to do, not absent.
    /// </summary>
    [Fact]
    public async Task IsPresentWithNothingToDoWhenOnlyTheFeedHasBeenFetched()
    {
        WriteFeed();

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("no releases", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every release is offered, the one the tooling still points at included. Deguffer cannot know
    /// whether a Functions v2 project is still on this machine, and withholding the row would be
    /// Deguffer taking that decision — Tier 2 and the age column are what put it back.
    /// </summary>
    [Fact]
    public async Task TargetsEveryDownloadedRelease()
    {
        var releases = CreateReleases("1.13.2", "2.60.0", "3.40.0", "4.18.1");
        RecordTag("v4", "4.18.1");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(4, plan.Steps.Count);
        Assert.Contains(Path.Combine(releases, "1.13.2"), plan.TargetedPaths);
        Assert.Contains(Path.Combine(releases, "4.18.1"), plan.TargetedPaths);
    }

    /// <summary>Every runtime line's numbering, including the long build numbers older feeds served.</summary>
    [Theory]
    [InlineData("4.0.5455")]
    [InlineData("2.7.1948")]
    [InlineData("1.13.2")]
    [InlineData("10.0.0")]
    public async Task RecognisesEveryFormOfReleaseVersion(string name)
    {
        var releases = CreateReleases(name);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(releases, name), plan.TargetedPaths);
    }

    /// <summary>
    /// §5.2's dangerous direction: a child this provider does not recognise must land in Tier 4 and
    /// stay out of the plan, however much it looks like a download.
    /// </summary>
    [Theory]
    [InlineData("4")]                    // a single number is not a version.
    [InlineData("4.18")]                 // two parts: fewer than the feed has ever served.
    [InlineData("4.0.5455.1")]           // four parts: more than the feed has ever served.
    [InlineData("v4")]                   // the tag name, not a release.
    [InlineData("4.18.1-backup")]        // something a person made.
    [InlineData("4.18.x")]               // not numeric throughout.
    [InlineData("X4.18.1")]              // prefixed: must not match unanchored.
    [InlineData("staging")]              // an unrelated directory.
    public async Task LeavesUnrecognisedChildrenAloneAndSaysSo(string name)
    {
        CreateReleases("4.18.1", name);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(
            plan.TargetedPaths,
            p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));

        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{name}' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => Path.GetFileName(p.Path) == name && p.ExistedBefore);
    }

    /// <summary>
    /// §5.6. The tooling's own bookkeeping sits one folder up from the downloads and looks exactly
    /// like more cache: the feed is a JSON file named for a sequence number, and <c>Tags</c> is four
    /// directories holding one short text file each. Both are how the tooling knows what it has.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheRootTheReleasesFolderTheTagsOrTheFeed()
    {
        CreateReleases("4.18.1");
        RecordTag("v4", "4.18.1");
        var feed = WriteFeed();

        var tags = Path.Combine(Root, AzureFunctionsToolTags.DirectoryName);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(Root, plan.TargetedPaths);
        Assert.DoesNotContain(Releases, plan.TargetedPaths);
        Assert.DoesNotContain(tags, plan.TargetedPaths);
        Assert.DoesNotContain(feed, plan.TargetedPaths);

        Assert.Contains(plan.ProtectedPaths, p => p.Path == Root && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Releases && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == tags && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == feed && p.ExistedBefore);
    }

    /// <summary>
    /// Tier 2, not Tier 1: nothing fetches a release back on its own, so the next project that needs
    /// one waits for a download. That must never be pre-selected for the user.
    /// </summary>
    [Fact]
    public void IsTierTwoSoItIsOfferedButNeverPreSelected()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.True(provider.Tier.IsOfferable());
        Assert.False(provider.Tier.IsPreSelectedByDefault());
    }

    /// <summary>
    /// The whole reason the tag records are read. A release the tooling still names and one it has
    /// stopped naming are the same shape on disk, and which is which is the fact a user decides on.
    /// </summary>
    [Fact]
    public async Task SaysWhichReleasesTheToolingStillNames()
    {
        CreateReleases("4.0.5455", "4.18.1");
        RecordTag("v4", "4.18.1");

        var plan = await CreateProvider().PlanAsync();

        var current = plan.Steps.OfType<DeleteStep>().Single(s => s.Path.EndsWith("4.18.1", StringComparison.Ordinal));
        var superseded = plan.Steps.OfType<DeleteStep>().Single(s => s.Path.EndsWith("4.0.5455", StringComparison.Ordinal));

        Assert.Contains("uses for Functions v4", current.What, StringComparison.Ordinal);
        Assert.Contains("no longer", superseded.What, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading no records is not the same as reading records that name nothing. Without the
    /// distinction, every row on a machine whose <c>Tags</c> folder is missing would claim the
    /// tooling had abandoned a release it uses daily.
    /// </summary>
    [Fact]
    public async Task DescribesAReleaseNeutrallyWhenThereAreNoTagRecordsToRead()
    {
        CreateReleases("4.18.1");

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteStep>());

        Assert.DoesNotContain("no longer", step.What, StringComparison.Ordinal);
        Assert.Contains("downloaded again", step.What, StringComparison.Ordinal);
    }

    /// <summary>
    /// "The tooling's own records no longer name this release" is a claim about every record there
    /// is, so it cannot be answered from a partial reading. A real machine has four tag directories,
    /// and one of them being a link nobody may look through leaves the other three able to produce
    /// a map that silently omits the release that unread one names — which would describe the
    /// release a developer's v2 projects use as abandoned, and offer it for deletion on that basis.
    /// </summary>
    [Fact]
    public async Task WillNotClaimARecordIsMissingWhenOneTagIsALink()
    {
        CreateReleases("4.18.1", "2.60.0");
        RecordTag("v4", "4.18.1");

        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "LastKnownGood-v2167102"), "2.60.0");

        var tags = Path.Combine(Root, AzureFunctionsToolTags.DirectoryName);
        Directory.CreateSymbolicLink(Path.Combine(tags, "v2"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(2, plan.Steps.Count);
        Assert.All(
            plan.Steps.OfType<DeleteStep>(),
            step => Assert.DoesNotContain("no longer", step.What, StringComparison.Ordinal));
    }

    /// <summary>The same, where Windows refuses one tag directory rather than the user relocating it.</summary>
    [Fact]
    public async Task WillNotClaimARecordIsMissingWhenOneTagWillNotBeRead()
    {
        CreateReleases("4.18.1", "2.60.0");
        RecordTag("v4", "4.18.1");
        RecordTag("v2", "2.60.0");

        using var denied = new DeniedDirectory(
            Path.Combine(Root, AzureFunctionsToolTags.DirectoryName, "v2"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(2, plan.Steps.Count);
        Assert.All(
            plan.Steps.OfType<DeleteStep>(),
            step => Assert.DoesNotContain("no longer", step.What, StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6 driven all the way through, which is the half a plan-only assertion cannot reach. The
    /// spared and the targeted are siblings in one folder, so this is exactly where an over-broad
    /// rule takes one with the other, and the plan naming a survivor is not evidence that it lived.
    /// </summary>
    [Fact]
    public async Task ExecutingTakesTheReleasesAndLeavesEverythingElseStanding()
    {
        CreateReleases("4.18.1", "3.40.0", "notes");
        RecordTag("v4", "4.18.1");
        var feed = WriteFeed();

        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "func.exe"), new byte[4096]);

        var link = Path.Combine(Releases, "2.60.0");
        Directory.CreateSymbolicLink(link, outside);

        string[] mustSurvive =
        [
            Root,
            Releases,
            Path.Combine(Root, AzureFunctionsToolTags.DirectoryName),
            Path.Combine(Releases, "notes"),
            link,
            outside,
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(Releases, "4.18.1")));
        Assert.False(Directory.Exists(Path.Combine(Releases, "3.40.0")));

        Assert.All(mustSurvive, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.True(File.Exists(feed), $"{feed} was removed");
        Assert.True(File.Exists(Path.Combine(outside, "func.exe")), "Deguffer deleted through a link.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7's age, and the fact the row is decided on once every release is offered. A release
    /// directory is written when it is downloaded and not rewritten by use, so this is when it
    /// arrived.
    /// </summary>
    [Fact]
    public async Task CarriesTheAgeOfEachRelease()
    {
        CreateReleases("4.18.1");

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps);

        Assert.NotNull(step.LastWritten);
        Assert.NotEqual("Unknown", RelativeAge.Describe(step.LastWritten, DateTime.UtcNow));
    }

    /// <summary>
    /// Moving 600 MB of releases onto another drive with a junction is ordinary, and the enumeration
    /// never classifies the directory it is handed: it would return the far side's children, target
    /// the ones whose names look like versions, and pass every §5.6 assertion, because each survivor
    /// named here resolves through the same link.
    /// </summary>
    [Fact]
    public async Task DeclinesTheToolingFolderWhenItIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Path.Combine(outside, AzureFunctionsToolsProvider.ReleasesName, "4.18.1");
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "payload.bin"), new byte[4096]);

        Directory.CreateDirectory(_environment.LocalAppData);
        Directory.CreateSymbolicLink(Root, outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        // Not HasUnreadableRoot: Windows refused nothing here. Deguffer declined, and the two states
        // send the reader to different places.
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>The same decline one level in, where only <c>Releases</c> was moved.</summary>
    [Fact]
    public async Task DeclinesTheReleasesFolderWhenItIsALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Path.Combine(outside, "4.18.1");
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "payload.bin"), new byte[4096]);

        Directory.CreateDirectory(Root);
        Directory.CreateSymbolicLink(Releases, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// One release relocated by junction leaves a folder that is present, measures zero, and says
    /// nothing about the hundreds of megabytes on the far side.
    /// </summary>
    [Fact]
    public async Task AReleasesFolderWhoseEveryChildIsALinkIsNotCalledClear()
    {
        CreateReleases();

        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "func.exe"), new byte[65536]);

        Directory.CreateSymbolicLink(Path.Combine(Releases, "4.18.1"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// The folder is found by name, and a listing right is separate from a traverse right — so a
    /// refusal here yields a plan with no steps, which the shell would otherwise render as "Already
    /// clear" about a folder nobody read.
    /// </summary>
    [Fact]
    public async Task AReleasesFolderThatWillNotBeListedIsSaidSoRatherThanLeftLookingAlreadyClear()
    {
        CreateReleases("4.18.1");

        using var denied = new DeniedDirectory(Releases);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(Releases));
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// A release holds its templates and its isolated worker runtimes several levels down, so a
    /// measurement that stopped at the top would report almost nothing.
    ///
    /// Not a §6.3 assertion, and not merely a weak one on a machine with <c>LongPathsEnabled</c>
    /// set: .NET applies <c>\\?\</c> itself past 260 characters, so the measurement succeeds however
    /// Core handles the path. <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>
    /// is the one test that would notice if that stopped being true. What this does prove is that
    /// the size reaches content nested inside a release rather than stopping at its first level.
    /// </summary>
    [Fact]
    public async Task MeasuresContentNestedDeeplyInsideARelease()
    {
        CreateReleases("4.18.1");

        var deep = Path.Combine(Releases, "4.18.1");
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('w', 40));
        }

        Assert.True(deep.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "worker.dll")), new byte[65536]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(Releases, "4.18.1"), plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 65536, "A release's nested content was not measured.");
    }
}
