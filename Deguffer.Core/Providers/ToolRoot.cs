using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A directory a provider owns, and the test that says which of its children are disposable.
///
/// <para>§5.2 is enforced inside a provider by <see cref="DisposableChildSet"/>, which the provider
/// consults while it builds its own plan. That is enough while the only route to a deletion is a
/// plan. §7.1 opens a second route: Explore draws every directory on the drive and lets the user
/// pick one out of the picture, and §5.2 is not scoped to a page — <c>gradle.properties</c> beside
/// <c>.gradle\caches</c> is Tier 4 there exactly as it is here. So the rule has to be readable from
/// outside the provider, and this is the shape it is read in.</para>
///
/// <para>It carries a predicate rather than a list of names because the providers do not all
/// classify by name. Playwright's children are versioned, so it matches a browser name and a
/// numeric revision instead, and a declaration that could only hold names would have had to leave
/// that provider out — which is the one direction §5.2 must never fail in.</para>
/// </summary>
/// <param name="Path">
/// The root itself, in display form. Never a target: a provider removes only what it recognises
/// inside, and Explore refuses the root for the same reason.
/// </param>
/// <param name="Reason">
/// Why the root must survive, written for the user. Explore states it when it refuses, because
/// §7.1 requires a refusal to say what it is rather than to grey a menu item out.
/// </param>
/// <param name="Recognises">
/// Whether a child of <paramref name="Path"/>, given by its name and by what it is on disk, is one
/// this provider's plan would offer. Anything else is Tier 4 by construction.
///
/// <para>The kind is part of the question because a plan's answer depends on it. A plan that lists
/// only folders never sees a file, and one that declines links never offers one, so a file or a
/// link carrying a recognised folder's name is something nobody classified, and Tier 4.</para>
/// </param>
/// <param name="Claim">
/// What the root says of what is inside it, which is how Explore words the refusal of an entry below
/// a probed root. Every root claims the whole folder except one from <see cref="Sparing"/>.
/// </param>
public sealed record ToolRoot(
    string Path,
    string Reason,
    Predicate<ToolRootChild> Recognises,
    ToolRootClaim Claim = ToolRootClaim.WholeFolder)
{
    /// <summary>
    /// The usual case: the provider already holds its rule as a child set, and its plan lists only
    /// folders.
    /// </summary>
    public static ToolRoot Of(string path, string reason, DisposableChildSet children)
    {
        ArgumentNullException.ThrowIfNull(children);

        return Folders(path, reason, children.IsDisposable);
    }

    /// <summary>
    /// A root whose plan offers folders only, and recognises them by name. A file or a link with a
    /// recognised name is refused, because the plan's own listing never sees the one and declines
    /// the other.
    /// </summary>
    public static ToolRoot Folders(string path, string reason, Predicate<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return new ToolRoot(path, reason, child => child.Kind == ChildKind.Folder && names(child.Name));
    }

    /// <summary>
    /// A folder a tool's setting names but the provider declines to examine as the tool's own, such as
    /// the profile or a drive root. The tool still writes its configuration there, so Explore refuses
    /// those entries, and nothing else: everything beside them is the user's, and refusing it would
    /// read the whole folder as the tool's.
    ///
    /// <para>Declared only through <see cref="ICleanupProvider.DiscoverToolRootsAsync"/>, which only ever
    /// adds a refusal. Declared roots at one folder are pooled, and a child any of them recognises is
    /// allowed, so a root recognising nearly everything would lift every other declaration's refusals
    /// there: two variables naming the same drive root would each open the other tool's credentials.</para>
    /// </summary>
    public static ToolRoot Sparing(string path, string reason, IEnumerable<string> configuration)
    {
        var names = configuration.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ToolRoot(path, reason, child => !names.Contains(child.Name), ToolRootClaim.NamedEntries);
    }

    /// <summary>
    /// A root for <paramref name="top"/> and for every folder between it and each of
    /// <paramref name="folders"/>, each recognising only the next folder on the way. Explore decides by
    /// the innermost root that refuses, so without the levels between, the top would refuse the way
    /// down or allow everything beside it.
    /// </summary>
    public static IEnumerable<ToolRoot> WayDown(string top, IEnumerable<string> folders, string reason)
    {
        var next = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [top] = new(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var folder in folders.Where(folder => LongPath.Contains(top, folder) && !folder.Equals(top, StringComparison.OrdinalIgnoreCase)))
        {
            var parent = top;

            foreach (var segment in System.IO.Path.GetRelativePath(top, folder).Split(System.IO.Path.DirectorySeparatorChar))
            {
                if (!next.TryGetValue(parent, out var names))
                {
                    next[parent] = names = new(StringComparer.OrdinalIgnoreCase);
                }

                names.Add(segment);
                parent = System.IO.Path.Combine(parent, segment);
            }
        }

        return next.Select(level => Folders(level.Key, reason, level.Value.Contains));
    }

}

/// <summary>What a <see cref="ToolRoot"/> says of the entries inside it.</summary>
public enum ToolRootClaim
{
    /// <summary>The folder is the tool's, so its reason is true of everything inside it.</summary>
    WholeFolder,

    /// <summary>Only the entries the root refuses are the tool's. The rest are someone else's.</summary>
    NamedEntries,
}
