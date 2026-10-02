namespace Deguffer.Testing;

/// <summary>
/// A folder that stands for another, of either kind, so a test of "never through a link" runs once
/// for each kind a user's machine can hold.
/// </summary>
public static class DirectoryLink
{
    /// <summary>
    /// Every <see cref="DirectoryLinkKind"/>, for <c>[MemberData]</c>. Read from the enum, so a kind
    /// added there reaches every test that runs over these.
    /// </summary>
    public static IEnumerable<object[]> Kinds =>
        Enum.GetValues<DirectoryLinkKind>().Select(kind => new object[] { kind });

    /// <summary>
    /// Each of <paramref name="cases"/> once per <see cref="DirectoryLinkKind"/>, as
    /// <c>(case, kind)</c> rows, for a theory that already runs over something else.
    /// </summary>
    public static IEnumerable<object[]> Across(params string[] cases) =>
        Across(cases.Select(@case => new object[] { @case }));

    /// <summary>
    /// Each row of <paramref name="rows"/> once per <see cref="DirectoryLinkKind"/>, with the kind
    /// appended as the last argument.
    /// </summary>
    public static IEnumerable<object[]> Across(IEnumerable<object[]> rows) =>
        from row in rows
        from kind in Enum.GetValues<DirectoryLinkKind>()
        select (object[])[.. row, kind];

    public static void Create(DirectoryLinkKind kind, string link, string target)
    {
        switch (kind)
        {
            case DirectoryLinkKind.SymbolicLink:
                SymbolicLink.ToDirectory(link, target);
                break;
            case DirectoryLinkKind.Junction:
                Junction.ToDirectory(link, target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of directory link.");
        }
    }
}
