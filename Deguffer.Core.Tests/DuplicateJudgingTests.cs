using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// A confirmed group judged once by the keeping rule (§7.4), on the search's thread, for its space,
/// its note and each copy's standing, so placing the group and drawing its rows ask the rule nothing.
/// </summary>
public sealed class DuplicateJudgingTests : DuplicateMarkingScene
{
    private string Desktop => Path.Combine(_tree.Environment.UserProfile, "Desktop");

    private static DuplicateGroup GroupOf(params DuplicateCandidate[] files) => new(MatchCriteria.Content, 100, Checksum: null, files);

    /// <summary>
    /// Each copy's keeping answer walks every protected place for each of its names, so a group is
    /// judged by one answer per copy, whichever of the space, the note and the rows reads it. The
    /// page once asked three or four times per copy.
    /// </summary>
    [Fact]
    public void JudgingAGroupAsksWhetherEachCopyCanBeKeptOnce()
    {
        var asked = 0;
        var nowhere = ResolvedPlaces.Resolve([], _tree.Volumes, FileInformation.Default);
        var keeping = new CopyKeeping(
            new CopyRefusals(_tree.Policy(), []),
            nowhere,
            nowhere,
            nowhere,

            // Asked once by every answer about a copy outside a reference location.
            _ =>
            {
                asked++;
                return StorageMedia.Nvme;
            });
        DuplicateCandidate[] files =
        [
            Copy(Path.Combine(Documents, "a.jpg")),
            Copy(Path.Combine(Downloads, "a.jpg")),
            Copy(Path.Combine(Desktop, "a.jpg")),
        ];

        var judged = GroupJudgement.Of(GroupOf(files), keeping);

        Assert.Equal(files.Length, asked);
        Assert.Equal(2 * 4096L, judged.FreeableSpace);
        Assert.Null(judged.WhyNothingCanBeKept);
        Assert.All(judged.Standings, standing => Assert.Equal(new CopyStanding(null, null), standing));
    }

    /// <summary>
    /// The judgement says what each separate question says: every copy's standing as the row lists
    /// it, and the group's note, for a reference copy, a copy on a USB disk, a copy in the temporary
    /// folder, an online-only copy and an ordinary one.
    /// </summary>
    [Fact]
    public void TheJudgementAnswersWhatEachQuestionAnswers()
    {
        DuplicateCandidate[] files =
        [
            Copy(Path.Combine(Documents, "a.jpg"), role: LocationRole.Reference),
            OnUsb("a.jpg"),
            Copy(Path.Combine(_tree.Environment.LocalAppData, "Temp", "a.jpg")),
            Copy(Path.Combine(Desktop, "a.jpg"), storage: FileStorage.CloudOnly),
            Copy(Path.Combine(Downloads, "a.jpg")),
        ];
        var marks = Marks(files);
        var group = Assert.Single(marks.Groups);

        var judged = marks.Judge(group.Group);

        Assert.Equal(files.Select(marks.Keeping.Standing), judged.Standings);
        Assert.Contains(judged.Standings, standing => standing.WhyNotMarked is not null);
        Assert.Contains(judged.Standings, standing => standing.WhyNotKept is not null);
        Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), judged.WhyNothingCanBeKept);

        // The reference copy is kept and is never marked, so every copy that may be marked could go.
        Assert.Null(marks.Keeping.WhyNotKept(files[0]));
        Assert.Equal(files.Where(copy => marks.Keeping.WhyNotMarked(copy) is null).Sum(copy => copy.SizeOnDisk), judged.FreeableSpace);
        Assert.True(judged.FreeableSpace >= 2 * 4096L);
    }

    /// <summary>A group no copy of which can be kept says so, and frees nothing.</summary>
    [Fact]
    public void AGroupThatCanKeepNoCopySaysSoAndFreesNothing()
    {
        var marks = Marks([OnUsb("a.jpg"), OnUsb("b.jpg")]);
        var group = Assert.Single(marks.Groups);

        var judged = marks.Judge(group.Group);

        Assert.Equal(0, judged.FreeableSpace);
        Assert.NotNull(judged.WhyNothingCanBeKept);
        Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), judged.WhyNothingCanBeKept);
    }

    /// <summary>
    /// A group judged by another search's keeping rule would be placed by a space these marks did
    /// not judge, so it is refused rather than placed.
    /// </summary>
    [Fact]
    public void AGroupJudgedByAnotherKeepingRuleIsRefused()
    {
        var result = Result([Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))]);
        var marks = DuplicateMarks.For(result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);
        var other = DuplicateMarks.For(result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);

        Assert.Throws<ArgumentException>(() => marks.Add(other.Judge(result.Groups[0])));
        Assert.Empty(marks.Groups);
    }

    /// <summary>
    /// The search judges groups on its own thread while the page marks copies on its own, and both
    /// remember each refusal, so groups judged from many threads at once are judged as from one.
    /// </summary>
    [Fact]
    public void GroupsJudgedFromManyThreadsAtOnceAreJudgedAsFromOne()
    {
        DuplicateCandidate[][] pairs =
        [
            .. Enumerable.Range(0, 6000).Select(i => new[]
            {
                Copy(Path.Combine(Documents, $"{i}", "a.jpg")),
                Copy(Path.Combine(_tree.Environment.LocalAppData, "Temp", $"{i}", "a.jpg")),
            }),
        ];
        var result = Result(pairs);
        DuplicateMarks Fresh() =>
            DuplicateMarks.For(result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);

        var alone = Fresh();
        var expected = result.Groups.Select(group => alone.Judge(group).Standings).ToList();
        var together = Fresh();
        var judged = new IReadOnlyList<CopyStanding>[result.Groups.Count];

        Parallel.For(
            0,
            result.Groups.Count,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => judged[i] = together.Judge(result.Groups[i]).Standings);

        Assert.Equal(expected, judged);
    }
}
