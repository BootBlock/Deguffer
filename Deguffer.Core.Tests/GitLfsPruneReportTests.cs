using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// What <c>git lfs prune --dry-run --verbose</c> printed, read back. The reports are git-lfs 3.8.1's,
/// captured from a redirected run with progress forced, with each object's identifier replaced; the
/// carriage returns and the padding to eighty columns are its own.
/// </summary>
public sealed class GitLfsPruneReportTests
{
    private const string First = "0b6136bde79c18535ede3634337afcec1ff1979d53c78c489b242c8328e3580a";
    private const string Second = "48ae46f75932ffcd885356cf89d89b6a743b51395ffebc45cb26ee7e2b5546d9";
    private const string Third = "f90fc00b718f3d12ef4b686c9c72b128ca527947b675bbd8ac467f0ae8cb9b5a";

    /// <summary>A progress update as git-lfs writes one: padded to the width and ended with a carriage return.</summary>
    private static string Update(string message) => message.PadRight(80) + "\r";

    private static string ForcedReport(string announced, params string[] objects) =>
        Update("1 local object, 0 retained") + Update("4 local objects, 0 retained") + Update("4 local objects, 1 retained")
        + "4 local objects, 1 retained, done.\n"
        + Update(announced) + "\n"
        + string.Concat(objects.Select(oid => Update($" * {oid} (300 KB)") + "\n"))
        + $" * {objects[^1]} (300 KB), done.\n";

    [Fact]
    public void ReadsEveryObjectOnceWhereTheLastIsPrintedTwice()
    {
        var preview = GitLfsPruneReport.TryRead(ForcedReport("3 files would be pruned (900 KB)", First, Second, Third));

        Assert.NotNull(preview);
        Assert.Equal([First, Second, Third], preview.Objects);
    }

    [Fact]
    public void ReadsOneObject()
    {
        var preview = GitLfsPruneReport.TryRead(ForcedReport("1 file would be pruned (300 KB)", First));

        Assert.Equal([First], preview?.Objects);
    }

    /// <summary>
    /// Without forced progress a redirected run names only the last object and drops the count, which
    /// is a report of one object where there are three. It must not be read as one.
    /// </summary>
    [Fact]
    public void RefusesARedirectedReportThatNamesOnlyTheLastObject()
    {
        const string redirected = "5 local objects, 2 retained, done.\n\n * " + Third + " (1.5 MB), done.\n";

        Assert.Null(GitLfsPruneReport.TryRead(redirected));
    }

    [Fact]
    public void RefusesAReportThatListsFewerObjectsThanItCounts()
    {
        Assert.Null(GitLfsPruneReport.TryRead(ForcedReport("3 files would be pruned (900 KB)", First, Second)));
    }

    [Fact]
    public void RefusesAReportThatListsMoreObjectsThanItCounts()
    {
        Assert.Null(GitLfsPruneReport.TryRead(ForcedReport("1 file would be pruned (300 KB)", First, Second)));
    }

    /// <summary>Only the counts line, when every object is kept.</summary>
    [Fact]
    public void ReadsAStoreWhoseObjectsAreAllKeptAsNothingToRemove()
    {
        var preview = GitLfsPruneReport.TryRead(Update("2 local objects, 2 retained") + "2 local objects, 2 retained, done.\n");

        Assert.Equal([], preview?.Objects);
    }

    /// <summary>A store with no object in it prints nothing at all, and exits 0.</summary>
    [Fact]
    public void ReadsNoOutputAsNothingToRemove()
    {
        Assert.Equal([], GitLfsPruneReport.TryRead(string.Empty)?.Objects);
    }

    /// <summary>
    /// git-lfs before 3.7.0 opens every progress line with <c>prune: </c>, and counts verified objects
    /// on the first where the remote was asked.
    /// </summary>
    [Fact]
    public void ReadsTheOlderPrefixAndTheVerifiedCount()
    {
        var report =
            "prune: 5 local objects, 2 retained, 2 verified with remote, done.\n"
            + "prune: 2 files would be pruned (600 KB)\n"
            + $" * {First} (300 KB)\n"
            + $" * {Second} (300 KB), done.\n";

        Assert.Equal([First, Second], GitLfsPruneReport.TryRead(report)?.Objects);
    }

    /// <summary>A list with no line that counts the store is not prune's report, whatever it looks like.</summary>
    [Fact]
    public void RefusesObjectsWithNoCountsLine()
    {
        Assert.Null(GitLfsPruneReport.TryRead($"1 file would be pruned (300 KB)\n * {First} (300 KB)\n"));
    }

    /// <summary>An identifier that is not a SHA-256 in lower-case hexadecimal is not one git-lfs stores, so the count no longer agrees.</summary>
    [Theory]
    [InlineData("0B6136BDE79C18535EDE3634337AFCEC1FF1979D53C78C489B242C8328E3580A")]
    [InlineData("0b6136bde79c18535ede3634337afcec1ff1979d53c78c489b242c8328e358")]
    [InlineData("..\\..\\..\\config")]
    public void RefusesAnObjectThatIsNotAnIdentifier(string named)
    {
        Assert.Null(GitLfsPruneReport.TryRead(ForcedReport("1 file would be pruned (300 KB)", named)));
    }
}
