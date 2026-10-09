using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>§7.4's marks: the per-group mark state that keeps a copy in every group, the named rules, and the space each group could free.</summary>
public sealed class DuplicateMarkingTests : DuplicateMarkingScene
{
    [Fact]
    public void NothingIsMarkedWhenASearchFinishes()
    {
        var marks = Marks(
            [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))],
            [Copy(Path.Combine(Documents, "b.jpg")), Copy(Path.Combine(Downloads, "b.jpg"))]);

        Assert.All(marks.Groups, group => Assert.Empty(group.Standing(marks.Keeping)));
        Assert.All(marks.Groups, group => Assert.All(group.Group.Files, file => Assert.False(group.IsMarked(file))));
    }

    [Fact]
    public void TheLastCopyThatCanBeKeptCannotBeMarkedByHand()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);
        var group = Only(marks);

        Assert.Null(group.Mark(files[0], marks.Keeping));
        Assert.NotNull(group.Mark(files[1], marks.Keeping));
        Assert.False(group.IsMarked(files[1]));
    }

    [Fact]
    public void ACopyThatCannotBeKeptLeavesTheOtherAsTheLastThatCanBe()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);
        var group = Only(marks);

        // The copy on the USB disk can be marked, and the internal one is then the last that can be kept.
        Assert.Null(group.Mark(files[1], marks.Keeping));
        Assert.NotNull(group.Mark(files[0], marks.Keeping));
    }

    public static TheoryData<string> RuleNames => ["newest", "oldest", "shortest path", "keep in folder", "mark in folder"];

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void NoRuleMarksTheLastCopyThatCanBeKept(string name)
    {
        // The only copy that can be kept is the oldest, with the longest path, outside the folder a
        // rule is told to keep, and inside the folder a rule is told to mark.
        var keptOnly = Copy(Path.Combine(Documents, "Long folder name", "a.jpg"), Older);
        var marks = Marks([keptOnly, OnUsb("a.jpg", Newest), Copy(InTemp("a.jpg"), Newer)]);

        MarkingRule rule = name switch
        {
            "newest" => new MarkingRule.KeepNewest(),
            "oldest" => new MarkingRule.KeepOldest(),
            "shortest path" => new MarkingRule.KeepShortestPath(),
            "keep in folder" => new MarkingRule.KeepInFolder(Downloads),
            _ => new MarkingRule.MarkInFolder(Path.GetDirectoryName(keptOnly.Path)!),
        };

        marks.Run(rule);

        Assert.Null(marks.Keeping.WhyNotKept(keptOnly));
        Assert.False(Only(marks).IsMarked(keptOnly));
    }

    /// <summary>
    /// A rule that keeps the copies in a folder marks nothing in a group with no copy there that can
    /// be kept, though the group could keep another.
    /// </summary>
    [Fact]
    public void KeepInAFolderMarksNothingInAGroupWithNoCopyThere()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.KeepInFolder(Path.Combine(Documents, "Camera")));

        Assert.Equal(0, outcome.Marked);
        Assert.All(files, file => Assert.False(Only(marks).IsMarked(file)));
    }

    /// <summary>
    /// A rule that marks the copies in a folder marks nothing in a group whose every copy that can be
    /// kept is there, rather than all but one of them.
    /// </summary>
    [Fact]
    public void MarkInAFolderMarksNothingWhereEveryCopyThatCanBeKeptIsThere()
    {
        var camera = Path.Combine(Documents, "Camera");
        DuplicateCandidate[] files = [Copy(Path.Combine(camera, "a.jpg")), Copy(Path.Combine(camera, "Old", "a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.MarkInFolder(camera));

        Assert.Equal(0, outcome.Marked);
        Assert.All(files, file => Assert.False(Only(marks).IsMarked(file)));
    }

    [Fact]
    public void TheGroupsAreSortedByTheSpaceEachCouldFree()
    {
        DuplicateCandidate[] Pair(string name, long size) =>
            [Copy(Path.Combine(Documents, name), sizeOnDisk: size), Copy(Path.Combine(Downloads, name), sizeOnDisk: size)];

        var marks = Marks(Pair("a.jpg", 4096), Pair("b.jpg", 8192), Pair("c.jpg", 16384));

        Assert.Equal(["c.jpg", "b.jpg", "a.jpg"], marks.Groups.Select(group => group.Group.Files[0].Name));
        Assert.Equal([16384L, 8192L, 4096L], marks.Groups.Select(group => group.FreeableSpace(marks.Keeping)));
    }

    /// <summary>
    /// Groups arrive one at a time while a search runs, and each is placed where the space it could
    /// free puts it, at the index the page inserts its row at, so no group already placed moves.
    /// Groups that could free the same stay in the order they arrived.
    /// </summary>
    [Fact]
    public void EachGroupIsPlacedAsItArrivesAndNoneMoves()
    {
        DuplicateCandidate[] Pair(string name, long size) =>
            [Copy(Path.Combine(Documents, name), sizeOnDisk: size), Copy(Path.Combine(Downloads, name), sizeOnDisk: size)];

        DuplicateCandidate[][] arriving = [Pair("a.jpg", 8192), Pair("b.jpg", 4096), Pair("c.jpg", 16384), Pair("d.jpg", 8192), Pair("e.jpg", 2048)];
        var result = Result(arriving);
        var marks = DuplicateMarks.For(result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);

        Assert.Empty(marks.Groups);

        List<string> shown = [];

        foreach (var group in result.Groups)
        {
            var (added, index) = marks.Add(group);

            Assert.Same(added, marks.Groups[index]);
            shown.Insert(index, group.Files[0].Name);
            Assert.Equal(shown, marks.Groups.Select(placed => placed.Group.Files[0].Name));
        }

        Assert.Equal(["c.jpg", "a.jpg", "d.jpg", "b.jpg", "e.jpg"], shown);
    }

    /// <summary>
    /// The keeping rule a group is placed by is read before the first group arrives, from the
    /// candidates: a drive the candidates are on is asked then, so a group on a USB disk is placed by
    /// what it could free there, which is nothing.
    /// </summary>
    [Fact]
    public void AGroupIsPlacedByTheKeepingRuleReadBeforeItArrived()
    {
        DuplicateCandidate[] internalPair = [Copy(Path.Combine(Documents, "a.jpg"), sizeOnDisk: 4096), Copy(Path.Combine(Downloads, "a.jpg"), sizeOnDisk: 4096)];
        DuplicateCandidate[] usbPair = [OnUsb("b.jpg"), OnUsb("c.jpg")];
        var result = Result(usbPair, internalPair);
        var marks = DuplicateMarks.For(result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);

        Assert.Equal(0, marks.Add(result.Groups[0]).Index);
        Assert.Equal(0, marks.Add(result.Groups[1]).Index);
        Assert.Equal([4096L, 0L], marks.Groups.Select(group => group.FreeableSpace(marks.Keeping)));
    }

    /// <summary>The oldest copy here is not the one with the shortest path, so the two rules keep different copies.</summary>
    [Fact]
    public void KeepTheOldestAndKeepTheShortestPathKeepDifferentCopies()
    {
        var oldest = Copy(Path.Combine(Documents, "Camera", "longer name", "a.jpg"), Older);
        var shortest = Copy(Path.Combine(Downloads, "a.jpg"), Newer);

        var byAge = Marks([oldest, shortest]);
        byAge.Run(new MarkingRule.KeepOldest());
        var byPath = Marks([oldest, shortest]);
        byPath.Run(new MarkingRule.KeepShortestPath());

        Assert.False(Only(byAge).IsMarked(oldest));
        Assert.True(Only(byAge).IsMarked(shortest));
        Assert.False(Only(byPath).IsMarked(shortest));
        Assert.True(Only(byPath).IsMarked(oldest));
    }

    [Fact]
    public void WhereNoCopyCanBeKeptEveryCopyStaysUnmarkedWithTheReason()
    {
        DuplicateCandidate[] files = [Copy(InTemp("a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);
        var group = Only(marks);

        Assert.NotNull(group.WhyNothingCanBeKept(marks.Keeping));
        Assert.All(files, file => Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), group.Mark(file, marks.Keeping)));
        Assert.All(files, file => Assert.NotNull(marks.Keeping.WhyNotKept(file)));

        var outcome = marks.Run(new MarkingRule.KeepNewest());

        Assert.Equal(0, outcome.Marked);
        Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), Assert.Single(outcome.Untouched).Reason);
    }

    [Fact]
    public void KeepTheNewestKeepsTheNewestCopyThatCanBeKeptAndMarksANewerOneOnAUsbDrive()
    {
        var older = Copy(Path.Combine(Documents, "a.jpg"), Older);
        var newer = Copy(Path.Combine(Downloads, "a.jpg"), Newer);
        var newest = OnUsb("a.jpg", Newest);
        var marks = Marks([older, newer, newest]);

        var outcome = marks.Run(new MarkingRule.KeepNewest());
        var group = Only(marks);

        Assert.Equal(2, outcome.Marked);
        Assert.False(group.IsMarked(newer));
        Assert.True(group.IsMarked(older));
        Assert.True(group.IsMarked(newest));
    }

    [Fact]
    public void AReferenceCopyAndAFileWithSeveralNamesAreNeverMarkedAndCanBeKept()
    {
        var reference = Copy(Path.Combine(Documents, "Photos", "a.jpg"), role: LocationRole.Reference);
        var linked = Copy(Path.Combine(Downloads, "a.jpg"), names: [Path.Combine(Downloads, "a.jpg"), Path.Combine(Downloads, "b.jpg")]);
        var other = Copy(Path.Combine(Downloads, "c.jpg"));
        var marks = Marks([reference, linked, other]);
        var group = Only(marks);

        Assert.Contains("reference", group.Mark(reference, marks.Keeping));
        Assert.Contains("2 names", group.Mark(linked, marks.Keeping));
        Assert.Null(marks.Keeping.WhyNotKept(reference));
        Assert.Null(marks.Keeping.WhyNotKept(linked));

        marks.Run(new MarkingRule.KeepNewest());

        Assert.False(group.IsMarked(reference));
        Assert.False(group.IsMarked(linked));
    }

    [Fact]
    public void EachRuleMarksWhatItSaysAndNeverACopyInACloudFolder()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");
        var camera = Path.Combine(Documents, "Camera");

        DuplicateCandidate[] Group() =>
        [
            Copy(Path.Combine(Documents, "a.jpg"), Older),
            Copy(Path.Combine(camera, "longer name", "a.jpg"), Newer),
            Copy(Path.Combine(Downloads, "a.jpg"), Newest),
            Copy(Path.Combine(oneDrive, "a.jpg"), Newest.AddDays(1)),
        ];

        void Expect(MarkingRule rule, params int[] marked)
        {
            var files = Group();
            var marks = Marks(files);
            marks.Run(rule);

            Assert.Equal(marked, Enumerable.Range(0, files.Length).Where(index => Only(marks).IsMarked(files[index])).ToArray());
        }

        Expect(new MarkingRule.KeepNewest(), 0, 1);
        Expect(new MarkingRule.KeepOldest(), 1, 2);
        Expect(new MarkingRule.KeepShortestPath(), 1, 2);
        Expect(new MarkingRule.KeepInFolder(camera), 0, 2);
        Expect(new MarkingRule.MarkInFolder(camera), 1);
    }

    [Fact]
    public void NoRuleMarksWhileAReferenceLocationWentUnsearched()
    {
        _unsearchedReferences.Add(new UnsearchedLocation(new SearchLocation(@"X:\Photos", LocationRole.Reference), "Windows would not open it."));
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg"), Older), Copy(Path.Combine(Downloads, "a.jpg"), Newer)];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.KeepNewest());

        Assert.Equal(0, outcome.Marked);
        Assert.NotNull(outcome.Refused);
        Assert.False(Only(marks).IsMarked(files[0]));

        // A mark by hand is one the user looked at, and stays open.
        Assert.Null(Only(marks).Mark(files[0], marks.Keeping));
    }

    [Fact]
    public void TheFreeableSpaceCountsAReferenceARefusedAndAMultiNameCopyAsNothing()
    {
        var marks = Marks(
            [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg")), Copy(Path.Combine(Downloads, "b.jpg"))],
            [
                Copy(Path.Combine(Documents, "c.jpg"), role: LocationRole.Reference),
                Copy(Path.Combine(_tree.System.ProgramFiles, "c.jpg")),
                Copy(Path.Combine(Downloads, "c.jpg"), names: [Path.Combine(Downloads, "c.jpg"), Path.Combine(Downloads, "d.jpg")]),
                Copy(Path.Combine(Downloads, "e.jpg")),
            ]);

        // Three markable copies, one kept: two can go. A reference, a refused and a linked copy free
        // nothing, so of four only the last can go, and the reference is what is kept.
        Assert.Equal([2 * 4096L, 4096L], marks.Groups.Select(group => group.FreeableSpace(marks.Keeping)));
    }
    /// <summary>
    /// Groups are placed as they arrive by what each could free, judged once a group. Placing each
    /// by asking the keeping rule again about every group already placed made a search of a few
    /// hundred groups take minutes on the page's thread, so four times the groups must cost about four
    /// times as much, never sixteen.
    /// </summary>
    [Fact]
    public void PlacingAGroupCostsTheSameHoweverManyArePlaced()
    {
        var few = Groups(100);
        var many = Groups(400);

        var growth = ThreadCpu.Growth(() => Place(few), () => Place(many), times: 4, TimeSpan.FromMilliseconds(250));

        Assert.True(growth < 2, $"Four times the groups cost {growth:F2} times as much as four runs of the few.");

        DuplicateCandidate[][] Groups(int count) =>
            [.. Enumerable.Range(0, count).Select(i => new[] { Copy(Path.Combine(Documents, $"{i}.jpg")), Copy(Path.Combine(Downloads, $"{i}.jpg")) })];

        void Place(DuplicateCandidate[][] groups) => Marks(groups);
    }
}
