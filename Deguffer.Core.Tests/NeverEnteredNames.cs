namespace Deguffer.Core.Tests;

/// <summary>
/// The directories a search through a source folder never enters unless it seeks them, for each
/// route's tests of that boundary.
///
/// <para>Written out here rather than read from <c>SourceTreeBoundary</c>: a name dropped from the
/// boundary then fails every route's test for it, where a list read from the boundary would drop
/// the test case along with the name. <c>.HG</c> is there because Windows matches a name in any
/// case, so the boundary has to.</para>
/// </summary>
public static class NeverEnteredNames
{
    public static TheoryData<string> All =>
        ["node_modules", ".git", ".hg", ".svn", ".bzr", ".jj", "_darcs", "__pycache__", ".HG"];
}
