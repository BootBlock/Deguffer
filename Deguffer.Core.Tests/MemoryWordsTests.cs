using Deguffer.Core.Memory;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a memory picture says in words: the two figures it leads with, what each part is, and what
/// this read could not measure. §7.2 makes all three a rule rather than presentation, so they are
/// tested: the numbers are lower bounds and say so, and nothing ever tells the reader to close, stop
/// or empty anything.
/// </summary>
public sealed class MemoryWordsTests
{
    private const long MiB = MemorySnapshotBuilder.MiB;

    /// <summary>
    /// What a "RAM cleaner" says, and what this must never say. Each is matched without regard to
    /// case, anywhere in the text.
    ///
    /// <para>This covers the sentences a memory <em>picture</em> writes, which is why §7.2's words are
    /// in Core at all. It is not turned on every sentence Core writes, and cannot be: §7.2.1's close
    /// asks a program to close, so its refusals, its confirmation and its report say "close" in almost
    /// every line, and the substrings here would read the one action the specification allows as the
    /// thing it forbids. Those sentences are held to §7.2.1 by
    /// <see cref="MemoryActionPolicyTests"/> and <see cref="MemoryClosePromptTests"/> instead, row by
    /// row. What the shell itself says is checked by driving it (G8).</para>
    /// </summary>
    private static readonly string[] NeverSaid =
    [
        "safe to", "close", "terminate", "kill", "stop it", "empty", "purge", "trim", "free up",
        "unneeded", "not needed", "wasting", "you should", "recommend",
    ];

    [Fact]
    public void TheHeadlineLeadsWithCommitChargeAgainstTheLimit()
    {
        var system = Snapshot().System;

        Assert.StartsWith(FreeSpace.Format(system.CommitCharge), MemoryHeadline.Commit(system), StringComparison.Ordinal);
        Assert.Contains(FreeSpace.Format(system.CommitLimit), MemoryHeadline.Commit(system), StringComparison.Ordinal);
        Assert.StartsWith(FreeSpace.Format(system.Available), MemoryHeadline.Available(system), StringComparison.Ordinal);

        // Which figure is which, and not only their order. Two sizes side by side say nothing about
        // what they are, and the headline is the one place §7.2 names a wording for.
        Assert.Contains("committed", MemoryHeadline.Commit(system), StringComparison.Ordinal);
        Assert.Contains("available", MemoryHeadline.Available(system), StringComparison.Ordinal);
    }

    /// <summary>The bar beside the words: half a limit is half, and nothing is ever past its end.</summary>
    [Theory]
    [InlineData(8_000, 16_000, 0.5)]
    [InlineData(20_000, 16_000, 1.0)]
    [InlineData(8_000, 0, 0)]
    public void TheCommittedFractionIsTheChargeAgainstTheLimit(long chargeMiB, long limitMiB, double expected)
    {
        var system = Snapshot().System with { CommitCharge = chargeMiB * MiB, CommitLimit = limitMiB * MiB };

        Assert.Equal(expected, MemoryHeadline.CommittedFraction(system), 3);
    }

    [Fact]
    public void EveryPartSaysWhatItIs() =>
        Assert.All(
            Enum.GetValues<MemoryPart>(),
            part => Assert.False(string.IsNullOrWhiteSpace(MemoryPartGuide.Describe(part)), $"{part} says nothing."));

    [Fact]
    public void NoPartTellsTheReaderToActOnIt() =>
        Assert.All(Enum.GetValues<MemoryPart>(), part => AssertSaysNothingToDo(MemoryPartGuide.Describe(part)));

    [Fact]
    public void TheNotesAlwaysSayTheFiguresAreLowerBounds() =>
        Assert.Contains("lower bound", MemoryNotes.For(Tree(Snapshot()))[0], StringComparison.Ordinal);

    /// <summary>
    /// A picture with no process in it is otherwise a picture of a machine running nothing, so each
    /// verdict says why in its own words.
    /// </summary>
    [Theory]
    [InlineData(ProcessFigures.NotOnThisArchitecture, "32-bit")]
    [InlineData(ProcessFigures.NothingToCheckAgainst, "no documented figure")]
    [InlineData(ProcessFigures.CreationTimeDisagrees, "did not agree")]
    [InlineData(ProcessFigures.PrivateWorkingSetDisagrees, "did not agree")]
    [InlineData(ProcessFigures.OwnProcessNotListed, "did not agree")]
    public void FiguresThatAreOffSayWhy(ProcessFigures figures, string expected)
    {
        var notes = MemoryNotes.For(Tree(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Figures(figures)
            .Build()));

        Assert.Contains(notes, note => note.Contains("No process is drawn", StringComparison.Ordinal)
            && note.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessTableReadInPartSaysSo()
    {
        var snapshot = Snapshot();
        var notes = MemoryNotes.For(Tree(snapshot with
        {
            Processes = snapshot.Processes with { Complete = false },
        }));

        Assert.Contains(notes, note => note.Contains("could not be read to its end", StringComparison.Ordinal));
    }

    /// <summary>
    /// Windows leaves services this account may not query out of its list without an error, so even a
    /// list read to its end has to say that some hosts are drawn as ordinary processes.
    ///
    /// <para>Each verdict is pinned by a phrase only it uses. All three share the "ordinary process"
    /// consequence, so asserting that alone would pass with the three answers collapsed into one — and
    /// telling a reader whose service list Windows refused outright that it was merely incomplete is
    /// the failure this is here to catch.</para>
    /// </summary>
    [Theory]
    [InlineData(ServiceListing.Listed, "without saying so")]
    [InlineData(ServiceListing.ListedInPart, "could not be read to its end")]
    [InlineData(ServiceListing.NotListed, "would not list its services")]
    public void EveryServiceListingSaysWhatItLeftOut(ServiceListing listing, string expected)
    {
        var snapshot = Snapshot();
        var notes = MemoryNotes.For(Tree(snapshot with
        {
            Services = snapshot.Services with { Listing = listing },
        }));

        Assert.Contains(notes, note => note.Contains("ordinary process", StringComparison.Ordinal)
            && note.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// With the per-process figures off, the two parts that hold processes are drawn standing at
    /// nothing. §7.2 will not have a zero pass for a measurement, so the notes say why those two are
    /// at nothing and where the memory went instead.
    /// </summary>
    [Fact]
    public void PartsLeftAtNothingSayWhyAndWhereTheMemoryWent()
    {
        var notes = MemoryNotes.For(Tree(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Figures(ProcessFigures.CreationTimeDisagrees)
            .Build()));

        Assert.Contains(notes, note => note.Contains("Applications and Services show nothing", StringComparison.Ordinal)
            && note.Contains("no figure attributes", StringComparison.Ordinal));
    }

    [Fact]
    public void ListsThatCouldNotBeUsedSayTheSystemCacheStandsForThem() =>
        Assert.Contains(
            MemoryNotes.For(Tree(Snapshot())),
            note => note.Contains("system cache is drawn", StringComparison.Ordinal));

    [Fact]
    public void FiguresThatOverlapSayByHowMuch()
    {
        var tree = Tree(new MemorySnapshotBuilder()
            .System(physicalMiB: 1_000, systemCacheMiB: 800, nonPagedPoolMiB: 100)
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Build());

        Assert.Contains(
            MemoryNotes.For(tree),
            note => note.Contains("overlap by", StringComparison.Ordinal)
                && note.Contains(FreeSpace.Format(200 * MiB), StringComparison.Ordinal));
    }

    [Fact]
    public void NoNoteTellsTheReaderToActOnAnything()
    {
        foreach (var figures in Enum.GetValues<ProcessFigures>())
        {
            foreach (var listing in Enum.GetValues<ServiceListing>())
            {
                var snapshot = new MemorySnapshotBuilder()
                    .Process(100, 1, "alpha.exe", 500, created: 10)
                    .Figures(figures)
                    .Build();

                var notes = MemoryNotes.For(Tree(snapshot with
                {
                    Services = snapshot.Services with { Listing = listing },
                }));

                Assert.All(notes, AssertSaysNothingToDo);
            }
        }
    }

    [Fact]
    public void AProcessHostingNoServiceKeepsItsOwnName() =>
        Assert.Equal("alpha.exe", ServiceHostText.Name("alpha.exe", []));

    [Fact]
    public void AHostOfOneServiceReadsAsThatService() =>
        Assert.Equal("svchost.exe: The Alpha Service", ServiceHostText.Name("svchost.exe", Services("Alpha")));

    /// <summary>
    /// A shape's label is trimmed to the width of the shape, so a host of several answers with how
    /// many it holds and <see cref="ServiceHostText.Holds"/> names them.
    /// </summary>
    [Fact]
    public void AHostOfSeveralServicesIsNamedByHowManyItHolds() =>
        Assert.Equal(
            "svchost.exe: 3 services",
            ServiceHostText.Name("svchost.exe", Services("Alpha", "Beta", "Gamma")));

    /// <summary>
    /// Read out to somebody rather than comma-separated throughout, because this is a sentence the
    /// reader is given rather than a list they scan.
    /// </summary>
    [Theory]
    [InlineData(2, "It holds The Alpha Service and The Beta Service.")]
    [InlineData(3, "It holds The Alpha Service, The Beta Service and The Gamma Service.")]
    public void AHostNamesTheServicesItHolds(int count, string expected) =>
        Assert.Equal(expected, ServiceHostText.Holds(Services("Alpha", "Beta", "Gamma").Take(count).ToArray()));

    /// <summary>The name already says it, and saying it twice reads as a second service.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AHostWithNoSecondServiceRepeatsNothing(int count) =>
        Assert.Empty(ServiceHostText.Holds(Services("Alpha").Take(count).ToArray()));

    /// <summary>
    /// What the <em>Services</em> part promises, kept where the reader asks for it. The part says a
    /// host is named by what it holds, and a host of several holds names no label can carry — so
    /// pointing at that host is what has to name them, and this is what fails if it stops.
    /// </summary>
    [Fact]
    public void PointingAtAHostOfSeveralServicesNamesThemAll()
    {
        var tree = Tree(new MemorySnapshotBuilder()
            .Process(300, 1, "svchost.exe", 200, created: 20)
            .Service("ExampleIndexer", host: 300)
            .Service("ExampleUpdater", host: 300)
            .Build());

        var said = MemoryPartGuide.Describe(tree, Host(tree, 300, created: 20));

        Assert.StartsWith(MemoryPartGuide.Describe(MemoryPart.Process), said, StringComparison.Ordinal);
        Assert.Contains("ExampleIndexer display name", said, StringComparison.Ordinal);
        Assert.Contains("ExampleUpdater display name", said, StringComparison.Ordinal);
        AssertSaysNothingToDo(said);
    }

    /// <summary>
    /// A host of one is already named by its service, and every process in <em>Applications</em>
    /// hosts none, so both get the part's own sentence and nothing appended to it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PointingAtAnythingElseSaysOnlyWhatItsPartIs(int services)
    {
        var built = new MemorySnapshotBuilder().Process(300, 1, "svchost.exe", 200, created: 20);

        if (services == 1)
        {
            built.Service("ExampleIndexer", host: 300);
        }

        var tree = Tree(built.Build());

        Assert.Equal(
            MemoryPartGuide.Describe(MemoryPart.Process),
            MemoryPartGuide.Describe(tree, Host(tree, 300, created: 20)));
    }

    private static void AssertSaysNothingToDo(string text) =>
        Assert.DoesNotContain(
            NeverSaid,
            forbidden => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    /// <summary>Services named the way a reader would recognise them, rather than by their key names.</summary>
    private static IReadOnlyList<RunningService> Services(params string[] names) =>
        [.. names.Select(name => new RunningService(name, $"The {name} Service", ProcessId: 300))];

    private static int Host(MemoryTree tree, int processId, long created) =>
        tree.Find(new MemoryNodeKey(MemoryPart.Process, processId, created))
            ?? throw new InvalidOperationException($"The tree has no process {processId}.");

    private static MemoryTree Tree(MemorySnapshot snapshot) => MemoryTreeBuilder.Build(snapshot);

    private static MemorySnapshot Snapshot() => new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 500, created: 10)
        .Build();
}
