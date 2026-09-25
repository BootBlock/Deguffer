using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a removal must leave behind, beyond the guard on recently changed files.
///
/// <para>All three members exist for §5.3's scratch folders, and neither can be expressed as an age.
/// <c>%TEMP%</c> is not a cache Deguffer may take away — every program on the machine expects the
/// folder itself to be there, and Windows does not put it back — so its <em>contents</em> are the
/// subject and the directory is not. And a folder a running program is working in is off limits
/// however old its files are, because the process holding it may have opened nothing this minute.
/// </para>
///
/// <para><b>They travel together because they are one caller's answer to one question.</b> A
/// removal that kept the root but forgot the exclusions would empty a live program's scratch
/// directory, and one that honoured the exclusions but not the root would delete the folder the
/// exclusions were inside. Passing them as one value is what stops half of that arriving.</para>
/// </summary>
/// <param name="KeepRoot">
/// Whether the directory named by the removal must itself survive. False for every ordinary
/// deletion, where the directory is the thing being removed.
/// </param>
/// <param name="Spared">
/// Paths this removal must not delete or descend into, in display form. Compared at every level
/// rather than only against the root's own children, so a caller sparing something nested is
/// honoured rather than silently ignored — the direction §5.2 requires an unrecognised case to fail
/// in.
/// </param>
/// <param name="OwnedElsewhere">
/// Paths this removal must not delete or descend into because another row offers them, in display
/// form. Held apart from <paramref name="Spared"/> because the two are reported differently: a spared
/// entry is one "something is using", and saying that of an entry the Node.js compile cache row owns
/// would send the user looking for a program that is not there.
/// </param>
public sealed record RemovalBounds(
    bool KeepRoot,
    IReadOnlyList<string> Spared,
    IReadOnlyList<string>? OwnedElsewhere = null)
{
    /// <summary>Nothing held back: the tree goes, root included.</summary>
    public static readonly RemovalBounds None = new(KeepRoot: false, []);

    /// <summary>
    /// The spared paths as a set the walk can ask cheaply, in the form
    /// <see cref="Safety.FileSystemEntry.FullName"/> arrives in.
    ///
    /// <para>Built once per removal rather than per entry: the walk asks this of every file and
    /// directory in a tree of hundreds of thousands, and a linear scan of a list there is per-entry
    /// work for an answer a hash lookup gives (G4).</para>
    ///
    /// <para><see cref="LongPath.Extended"/> on the way in, because an enumeration below an extended
    /// root yields extended children, and a set holding display paths would match none of them —
    /// which fails silently and in the dangerous direction: every spared path would be deleted.</para>
    /// </summary>
    internal IReadOnlySet<string> SparedPaths { get; } = ExtendedSet(Spared);

    /// <summary>
    /// <see cref="OwnedElsewhere"/> as a set the walk can ask cheaply, on the terms
    /// <see cref="SparedPaths"/> gives.
    /// </summary>
    internal IReadOnlySet<string> OwnedElsewherePaths { get; } = ExtendedSet(OwnedElsewhere ?? []);

    private static HashSet<string> ExtendedSet(IReadOnlyList<string> paths) =>
        new(paths.Select(LongPath.Extended), StringComparer.OrdinalIgnoreCase);
}
