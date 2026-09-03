namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Whether Explore will remove a path, and why not when it will not.
///
/// <para>A record rather than a bare bool because §7.1 requires the refusal to <em>say</em> what it
/// is. Explore draws every directory on the drive, so a user who picked one out of the picture and
/// found the menu item greyed out would learn nothing about which of the many possible reasons
/// applied — and the one reading a size picture is exactly the user least likely to guess.</para>
/// </summary>
/// <param name="IsAllowed">Whether the removal may go ahead.</param>
/// <param name="Reason">
/// Written for the user. On a refusal it is the sentence shown; on an allowance it says what the
/// user is being told about the thing, which §7.1 is careful is never the word "safe".
/// </param>
public sealed record ExploreVerdict(bool IsAllowed, string Reason)
{
    /// <summary>
    /// What Explore says about a path it has no knowledge of, which is most of a drive.
    ///
    /// <para>§7.1: "A path Explore does not recognise is <em>unclassified</em>, not <em>safe</em>."
    /// Nothing here has classified anything — Explore reports a name and a number — so the sentence
    /// the user is given says exactly that, and the tier language that belongs to Storage stays on
    /// the Storage page.</para>
    /// </summary>
    public static readonly ExploreVerdict Unclassified = new(
        IsAllowed: true,
        "Deguffer has not classified this. It knows only its name and its size, so what removing it "
        + "costs is yours to judge.");

    public static ExploreVerdict Refuse(string reason) => new(IsAllowed: false, reason);
}

/// <summary>Whether a <see cref="ProtectedRegion"/> covers the path alone or the tree under it.</summary>
public enum RegionScope
{
    /// <summary>
    /// The path itself. What is inside it is decided by whatever region covers that, which is how a
    /// directory can be undeletable while the things in it are ordinary — a drive root, and the
    /// user's own profile, are both that shape.
    /// </summary>
    PathOnly,

    /// <summary>The path and everything below it.</summary>
    PathAndBelow,
}

/// <summary>
/// One entry in the table Explore decides removals from: a path, how far the entry reaches, and
/// what it says.
///
/// <para>Entries may overlap, and the most specific one wins — the longest path, and at equal
/// length <see cref="RegionScope.PathOnly"/> ahead of <see cref="RegionScope.PathAndBelow"/>. That
/// is what lets the table state a rule and its exception without either being written as a special
/// case: <c>C:\Users</c> is refused with everything under it, the signed-in user's own profile is
/// permitted below, and the profile directory itself is refused again on its own.</para>
/// </summary>
/// <param name="Path">The path this entry is about, fully qualified and without a trailing separator.</param>
/// <param name="Scope">How far it reaches.</param>
/// <param name="Verdict">What it says. A permitting entry ends the table's search, not the tool-root check that follows it.</param>
public sealed record ProtectedRegion(string Path, RegionScope Scope, ExploreVerdict Verdict)
{
    /// <summary>A refusal, which is what most entries are.</summary>
    public static ProtectedRegion Refusing(string path, RegionScope scope, string reason) =>
        new(path, scope, ExploreVerdict.Refuse(reason));

    /// <summary>
    /// A permission, which exists only to carve a hole in a broader refusal above it. It says
    /// nothing about tool roots, which are checked afterwards and separately.
    /// </summary>
    public static ProtectedRegion Permitting(string path, RegionScope scope) =>
        new(path, scope, ExploreVerdict.Unclassified);
}
