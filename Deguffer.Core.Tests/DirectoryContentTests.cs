using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6 asks <see cref="DirectoryContent.IsPresent"/> of every protected directory twice, when the
/// plan is made and after the run, so what it answers for each shape is the whole of what an
/// emptied-in-place over-reach can be caught by.
///
/// <para>The two directions cost different things. Content read where there is none records a folder
/// as having held something, and its survival then proves nothing. No content read where there is
/// some turns an untouched folder into an alarm, and teaches the reader to ignore the next one.</para>
/// </summary>
public sealed class DirectoryContentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFileAtAnyDepthIsContent()
    {
        var shallow = _temp.CreateDirectory("shallow");
        _temp.CreateFile(8, "shallow", "one.bin");

        var deep = _temp.CreateDirectory("deep");
        _temp.CreateFile(8, "deep", "a", "b", "c", "one.bin");

        Assert.True(DirectoryContent.IsPresent(shallow));
        Assert.True(DirectoryContent.IsPresent(deep));
    }

    /// <summary>
    /// The shape a removal that took every file leaves behind. A question asked of the top level sees
    /// a folder holding folders, and calls a folder that lost everything a survivor.
    /// </summary>
    [Fact]
    public void FoldersHoldingOnlyEmptyFoldersAreNotContent()
    {
        var skeleton = _temp.CreateDirectory("skeleton");
        _temp.CreateDirectory("skeleton", "a", "b");
        _temp.CreateDirectory("skeleton", "c");

        Assert.False(DirectoryContent.IsPresent(skeleton));
        Assert.False(DirectoryContent.IsPresent(_temp.CreateDirectory("empty")));
    }

    /// <summary>A file is not a directory holding something, and neither is a path that is not there.</summary>
    [Fact]
    public void AFileOrAMissingPathIsNotADirectoryWithContent()
    {
        Assert.False(DirectoryContent.IsPresent(_temp.CreateFile(8, "one.bin")));
        Assert.False(DirectoryContent.IsPresent(Path.Combine(_temp.Path, "never-existed")));
    }

    /// <summary>
    /// A link is an entry somebody put there, so removing it is a loss whatever it points at. It
    /// points at an empty folder here, which is what makes the test discriminate: a walk that
    /// descended into the link rather than counting it would find nothing.
    ///
    /// <para>That the link is not <em>followed</em> is not observable through a boolean once the link
    /// itself counts, so this test does not claim it.</para>
    /// </summary>
    [Fact]
    public void ALinkIsContentWhateverItPointsAt()
    {
        var holder = _temp.CreateDirectory("holder");
        Directory.CreateSymbolicLink(Path.Combine(holder, "link"), _temp.CreateDirectory("outside-empty"));

        Assert.True(DirectoryContent.IsPresent(holder));
    }

    /// <summary>
    /// The refusal that decides whether the check cries wolf. A directory Windows will not list was
    /// never seen, so it is never recorded as having held content, and it can never be reported as
    /// emptied (§5.3).
    /// </summary>
    [Fact]
    public void ADirectoryThatWillNotBeListedHoldsNothingRatherThanRaisingAnAlarm()
    {
        var directory = _temp.CreateDirectory("denied");
        _temp.CreateFile(8, "denied", "inside.bin");

        Assert.True(DirectoryContent.IsPresent(directory));

        using var denied = new DeniedDirectory(directory);

        Assert.False(DirectoryContent.IsPresent(directory));
    }

    /// <summary>
    /// The opposite case below the top level. A subdirectory that will not be listed was seen, so
    /// something is there. It is empty here, which is what makes the test discriminate: read as
    /// empty, the folder holding it would report no content, and the moment the files beside it
    /// went, a refusal would become an alarm.
    /// </summary>
    [Fact]
    public void ASubdirectoryThatWillNotBeListedIsContent()
    {
        var holder = _temp.CreateDirectory("holder");
        var refused = _temp.CreateDirectory("holder", "refused");

        Assert.False(DirectoryContent.IsPresent(holder));

        using var denied = new DeniedDirectory(refused);

        Assert.True(DirectoryContent.IsPresent(holder));
    }

    /// <summary>
    /// §6.3: the only content sits past <c>MAX_PATH</c>. A crash guard rather than a discriminating
    /// test, on the reasoning CLAUDE.md's G8 records: .NET prefixes a long path itself, and the answer
    /// is a boolean, so the form of the paths the walk builds cannot be observed from here.
    /// </summary>
    [Fact]
    public void ReachesAFilePastMaxPath()
    {
        var deep = Path.Combine(_temp.Path, "deep");
        while (deep.Length < 280)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "one.bin")), new byte[8]);

        var outermost = Path.Combine(_temp.Path, "deep");
        Assert.True(outermost.Length < 260);
        Assert.True(DirectoryContent.IsPresent(outermost));
    }
}
