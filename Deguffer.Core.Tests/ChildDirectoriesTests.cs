using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sole directory-enumeration seam in Core, and the only place two safety facts are applied:
/// a link is separated from a real directory rather than followed, and a root that will not answer
/// is reported as not having answered.
///
/// It had four hand-written copies and no test file of its own before this one, which is how the
/// second fact came to be missing from every caller for as long as it was.
/// </summary>
public sealed class ChildDirectoriesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SeparatesLinksFromRealDirectoriesWithoutFollowingThem()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "real");
        _temp.CreateDirectory("outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(_temp.Path, "outside"));

        var scan = ChildDirectories.Under(root);

        Assert.Equal("real", Assert.Single(scan.Directories).Name);
        Assert.Equal("link", Assert.Single(scan.Links).Name);
        Assert.False(scan.Unreadable);
    }

    [Fact]
    public void AnEmptyRootIsReadableAndHasNoChildren()
    {
        var scan = ChildDirectories.Under(_temp.CreateDirectory("empty"));

        Assert.Empty(scan.Directories);
        Assert.Empty(scan.Links);
        Assert.False(scan.Unreadable);
    }

    /// <summary>
    /// The distinction ten callers could not make. A root that refuses to be listed yields the same
    /// two empty lists an empty root does, so every caller that reported "there is nothing here"
    /// was reporting a listing it never obtained.
    ///
    /// <see cref="IDirectoryScanner.TryFindDirectoriesNamedAsync"/> already states the rule this
    /// answers to — an empty list a caller cannot tell from "there are none" is not an answer — and
    /// this seam is the one place in Core that broke it.
    /// </summary>
    [Fact]
    public void ARootThatWillNotBeListedIsReportedAsUnreadableRatherThanEmpty()
    {
        var root = _temp.CreateDirectory("refused");
        _temp.CreateDirectory("refused", "child");

        using var denied = new DeniedDirectory(root);

        var scan = ChildDirectories.Under(root);

        Assert.True(scan.Unreadable);
        Assert.Empty(scan.Directories);
        Assert.Empty(scan.Links);
    }

    /// <summary>
    /// A root that is not there is empty, not unreadable, and the distinction is the whole point of
    /// the flag. "Deguffer could not list this folder" is a sentence about permissions; a folder
    /// that does not exist holds nothing, which is a complete answer rather than the absence of one.
    ///
    /// <para>Two callers reach here without checking existence first, and both would say the wrong
    /// thing otherwise: the discovery walk pops a path a build has since removed, and the sweep of
    /// the two application-data roots takes them as configured. Neither is a refusal, and neither
    /// should raise a warning about permissions.</para>
    /// </summary>
    [Fact]
    public void AMissingRootIsEmptyRatherThanUnreadable()
    {
        var scan = ChildDirectories.Under(Path.Combine(_temp.Path, "never-created"));

        Assert.False(scan.Unreadable);
        Assert.Empty(scan.Directories);
    }

    /// <summary>
    /// §6.3, at the one seam where the prefix is observable without a test double.
    ///
    /// <para>An outcome-based long-path test cannot fail. .NET prepends <c>\\?\</c> itself to any
    /// path of 260 characters or more before it calls Win32 — measured directly: in a process where
    /// <c>RtlAreLongPathsEnabled</c> reports 0, a raw <c>CreateDirectoryW</c> on a 377-character
    /// path fails with <c>ERROR_PATH_NOT_FOUND</c> while <c>Directory.CreateDirectory</c> on the
    /// same path succeeds. So building a deep tree and asserting the enumeration reached it passes
    /// identically with <see cref="LongPath.Extended"/> deleted outright, on every machine and
    /// regardless of the <c>LongPathsEnabled</c> registry value.</para>
    ///
    /// <para>The form of the path is what discriminates. .NET builds each child from the parent it
    /// was given, so a prefixed root hands back prefixed children — which is the property every
    /// caller of this seam then relies on, because each one turns a child straight into a deletion
    /// target or a directory to descend into.</para>
    /// </summary>
    [Fact]
    public void HandsBackChildrenInTheExtendedLengthFormItsCallersGoOnToUse()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "child");
        Directory.CreateSymbolicLink(
            Path.Combine(root, "link"),
            _temp.CreateDirectory("outside"));

        var scan = ChildDirectories.Under(root);

        var children = scan.Directories.Concat(scan.Links).ToList();

        // Assert.All passes over an empty sequence, so the count comes first: a regression that
        // returned nothing at all would otherwise turn this into a green no-op.
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.StartsWith(@"\\?\", child.FullName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The prefix survives a root that already carries it, so a walk that extended once does not
    /// pay for it per directory — and, more to the point, does not double it into a path nothing
    /// can resolve.
    /// </summary>
    [Fact]
    public void AcceptsARootThatIsAlreadyInExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "child");

        var scan = ChildDirectories.Under(LongPath.Extended(root));

        Assert.Equal("child", Assert.Single(scan.Directories).Name);
        Assert.False(scan.Unreadable);
    }
}
