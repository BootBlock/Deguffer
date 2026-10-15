using System.Collections.Concurrent;
using System.Collections.Frozen;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>What a walk of one approved source root found.</summary>
/// <param name="Candidates">Every directory of a sought name below the root, in display form and ordinal order.</param>
/// <param name="Unreadable">Every directory Windows would not list, in display form and ordinal order.</param>
/// <param name="RootListed">Whether the root itself was listed.</param>
internal sealed record WalkedSourceTree(
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Unreadable,
    bool RootListed);

/// <summary>
/// The guaranteed route through an approved source root: list it ourselves, on
/// <see cref="BoundedFileWalk"/>'s workers, applying <see cref="SourceTreeBoundary"/> as each
/// directory is read.
///
/// <para>On the bounded walk rather than a loop of its own because the trees this runs over are the
/// developer's whole source folder, which on one thread is every directory listed in turn while the
/// rest of the machine's cores wait. The walk also holds the rule that a link is never descended
/// into, so this cannot lose it.</para>
///
/// <para>The workers find candidates in whatever order they happen to run, so both lists are sorted
/// before they are handed back. A plan built from them then reads the same on every run, and the same
/// at one thread as at sixteen.</para>
/// </summary>
internal static class SourceTreeWalk
{
    /// <param name="root">An approved source root, in either form.</param>
    /// <param name="search">
    /// The search for every name the discovery looks for. See <see cref="SourceDirectoryDiscovery.Include"/>.
    /// </param>
    /// <param name="tuning">What the walk runs with, resolved for the drive that holds <paramref name="root"/>.</param>
    public static WalkedSourceTree Under(
        string root,
        DirectorySearch search,
        WalkTuning tuning,
        CancellationToken ct)
    {
        var candidates = new ConcurrentBag<string>();
        var unreadable = new ConcurrentBag<string>();
        var top = LongPath.Extended(root);

        // Spelled as the walk spells a child, so a handed-off directory is recognised by one lookup.
        var handedOff = search.HandedOff
            .Select(belowRoot => Path.Join(top, string.Join(Path.DirectorySeparatorChar, belowRoot)))
            .ToFrozenSet(StringComparer.Ordinal);

        // Written by the root's own listing alone, before anything below it is queued.
        var rootListed = true;

        // Each directory carries whether it is the root, the one thing its listing does not say. No
        // file is listed, since only folders are sought.
        BoundedFileWalk.Visit(
            top,
            rootState: true,
            tuning,
            FileNames.None,
            (isRoot, contents, descend) =>
            {
                if (contents.WasRefused)
                {
                    // A directory gone since its parent was listed holds nothing, which is a complete
                    // answer. One Windows would not list left everything below it unsearched, and
                    // silence would leave the plan describing a sweep of the whole approved root.
                    // Nothing a refused listing returned part-way is used: a folder is searched whole
                    // or reported, never half of each.
                    if (contents.Presence is PathPresence.Refused)
                    {
                        unreadable.Add(LongPath.Display(contents.Directory));
                        rootListed &= !isRoot;
                    }

                    return;
                }

                foreach (var child in contents.Entries)
                {
                    // Asked although no file is listed: a candidate is a folder, whatever a listing
                    // through another seam hands back.
                    if (!child.IsDirectory)
                    {
                        continue;
                    }

                    switch (search.Meet(child.Name))
                    {
                        // Not descended into: anything below a candidate belongs to that candidate,
                        // and an obj nested inside an obj is part of the output already being
                        // considered.
                        case SearchStep.Found:
                            candidates.Add(LongPath.Display(child.FullName));
                            break;

                        // The path is built to ask only where something is handed off, since the walk
                        // builds it again to descend.
                        case SearchStep.Entered when handedOff.Count == 0 || !handedOff.Contains(child.FullName):
                            descend(child, false);
                            break;
                    }
                }
            },
            static () => { },
            TimeProvider.System,
            ct);

        // Ordinal, so two names that differ only in case in a case-sensitive folder still come back
        // in one order.
        return new WalkedSourceTree(
            [.. candidates.Order(StringComparer.Ordinal)],
            [.. unreadable.Order(StringComparer.Ordinal)],
            rootListed);
    }
}
