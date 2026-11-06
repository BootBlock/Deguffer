using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// A duplicate search asks where a copy's volume is mounted once for each folder the copies are in,
/// rather than once for each copy, and judges each copy by where its volume really is mounted
/// (§7.1, §7.4, #539). Explore's own policy still asks about each item at the question.
/// </summary>
public sealed class CopyMountPointTests : DuplicateMarkingScene
{
    private const int Copies = 100;

    private string Mount => Path.Combine(_tree.Top, "Mount");

    /// <summary>
    /// A hundred copies in one folder, judged as the search confirms their group, ask Windows where the
    /// folder is mounted once, and every place its volume is mounted once. Each copy asked both before,
    /// a hundred times for one answer.
    /// </summary>
    [Fact]
    public void ASearchOfManyCopiesInOneFolderAsksWhereItIsMountedOnce()
    {
        DuplicateCandidate[] copies =
        [
            .. Enumerable.Range(0, Copies).Select(index => Copy(Path.Combine(Documents, $"copy{index}.jpg"))),
        ];
        // What the marks ask about the places they read themselves, with no copy to judge.
        Marks();
        var withoutCopies = _tree.Volumes.MountPointsQueries.Count;
        _tree.Volumes.MountPointsQueries.Clear();
        _tree.Volumes.MountPointQueries.Clear();

        var marks = Marks(copies);

        Assert.All(copies, copy => Assert.Null(marks.Keeping.WhyNotMarked(copy)));
        Assert.Single(_tree.Volumes.MountPointQueries, path => path.StartsWith(Documents, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(withoutCopies + 1, _tree.Volumes.MountPointsQueries.Count);
    }

    /// <summary>
    /// Explore's own policy is asked about each item at the question, so what it removes is judged by
    /// where the item's volume is mounted at that moment, however many items share its folder.
    /// </summary>
    [Fact]
    public void ExplorePolicyAsksWhereEachItemIsMounted()
    {
        var policy = _tree.Policy();

        for (var index = 0; index < Copies; index++)
        {
            Assert.True(policy.MayRemove(Path.Combine(Documents, $"copy{index}.jpg")).IsAllowed);
        }

        Assert.Equal(Copies, _tree.Volumes.MountPointQueries.Count(path => path.StartsWith(Documents, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A volume mounted at a folder while the search runs is seen by every copy in that folder judged
    /// after it, so the volume's paging file is refused as the top of the volume it is (§5.2), and the
    /// rest of the volume stays ordinary (§5.6). The folders judged before the mount keep their answers,
    /// which no mount can change while they hold a copy: Windows mounts a volume only at an empty folder.
    /// </summary>
    [Fact]
    public void ACopyBelowAFolderMountedDuringTheSearchIsJudgedByWhereItIsMounted()
    {
        var before = Copy(Path.Combine(Documents, "a.jpg"));
        var paging = Copy(Path.Combine(Mount, "pagefile.sys"));
        var photo = Copy(Path.Combine(Mount, "Holiday photos", "a.jpg"));
        var refusals = new CopyRefusals(_tree.Policy(), [], [before, paging, photo]);

        // Before the mount, a file called pagefile.sys in a folder of the drive is an ordinary file.
        Assert.Null(new CopyRefusals(_tree.Policy(), [], [paging]).WhyRefused(paging));
        Assert.Null(refusals.WhyRefused(before));

        _tree.Volumes.With(Mount + Path.DirectorySeparatorChar);

        Assert.NotNull(refusals.WhyRefused(paging));
        Assert.Null(refusals.WhyRefused(photo));
        Assert.Null(refusals.WhyRefused(before));
    }

    /// <summary>
    /// The policy a search judges its copies by still reads a folder a volume is mounted at as that
    /// whole volume, though a file beside the folder was judged first: only the files it was given
    /// are answered by their folder.
    /// </summary>
    [Fact]
    public void AFolderAVolumeIsMountedAtIsAWholeDriveThoughAFileBesideItWasJudged()
    {
        var beside = Path.Combine(_tree.Top, "a.jpg");
        _tree.Volumes.With(Mount + Path.DirectorySeparatorChar);
        var policy = _tree.Policy().JudgingFiles([beside]);

        Assert.True(policy.MayRemove(beside).IsAllowed);
        Assert.False(policy.MayRemove(Mount).IsAllowed);
    }
}
