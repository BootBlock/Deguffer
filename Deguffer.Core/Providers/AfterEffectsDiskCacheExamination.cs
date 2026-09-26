using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One version's disk cache a plan may offer.</summary>
/// <param name="Path">The cache folder, named for this computer.</param>
/// <param name="VersionFolder">The version's folder it is in, which must survive.</param>
internal sealed record AfterEffectsDiskCache(string Path, string VersionFolder);

/// <summary>
/// What one look below every folder After Effects was told to cache in found: this computer's cache
/// for each version, and everything that must survive. The one pass the plan, Explore's refusals and
/// the temporary-folder row's claim are all built from, so none of them can disagree about what is
/// After Effects' cache.
///
/// <para><b>Nothing inside a temporary folder is named as a survivor.</b> After Effects often caches
/// in <c>%TEMP%</c>, and the temporary-files row may remove whatever else is there once nothing has
/// touched it for long enough, the folders above the cache included. Asserting here that they survive
/// would report that row's ordinary run as this one's failure (§5.6), so this row takes only its cache
/// there, and leaves everything else to that row's rules. The cost is that §5.6 has nothing of this
/// row's to check there.</para>
///
/// <para><b>The chosen folder is never named.</b> It is the user's, often one they keep other things
/// in, and naming it would refuse everything in it in Explore. What After Effects made inside it,
/// <c>Adobe\After Effects</c> and below, is named.</para>
/// </summary>
internal sealed class AfterEffectsDiskCacheExamination
{
    private IReadOnlyList<string> _temporaryFolders = [];

    public List<AfterEffectsDiskCache> Caches { get; } = [];

    public List<(string Path, string Reason)> Survivors { get; } = [];

    /// <summary>
    /// Links left alone where the cache or a folder above it was expected. A row that offers nothing
    /// must not read as clear while one is there.
    /// </summary>
    public List<string> Declined { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    public bool Unreadable { get; private set; }

    /// <summary>The name of this computer's cache, the one child of a version folder that is a target.</summary>
    public string CacheName { get; private set; } = "";

    /// <param name="folders">Each folder After Effects' preferences name for the disk cache.</param>
    /// <param name="temporaryFolders">The folders the temporary-files row empties, where the survivors are not this row's to name.</param>
    public static AfterEffectsDiskCacheExamination Of(
        IReadOnlyList<string> folders,
        string machineName,
        IReadOnlyList<string> temporaryFolders,
        CancellationToken ct)
    {
        var examination = new AfterEffectsDiskCacheExamination
        {
            CacheName = AfterEffectsDiskCacheLayout.CacheName(machineName),
            _temporaryFolders = [.. temporaryFolders.Select(folder => LongPath.Unaliased(folder))],
        };

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            examination.Collect(folder, ct);
        }

        return examination;
    }

    /// <summary>
    /// One chosen folder: the folder After Effects makes below it, reached without passing through a
    /// link, and each version in it.
    /// </summary>
    private void Collect(string folder, CancellationToken ct)
    {
        var versions = AfterEffectsDiskCacheLayout.VersionsUnder(folder);

        // The chosen folder is taken as the preferences give it, link or not, since that is where
        // After Effects writes. What was built below it is checked for links at every step.
        if (DerivedPath.FirstObstacleBetween(folder, versions) is { } obstacle)
        {
            if (obstacle.IsLink)
            {
                DeclineLink(obstacle.Path);
            }
            else
            {
                Unreached(obstacle.Path);
            }

            return;
        }

        switch (LongPath.ProbeDirectory(versions))
        {
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                Unreached(versions);
                return;
        }

        var children = ChildDirectories.Under(versions);

        if (children.Unreadable)
        {
            Unlisted(versions);
            return;
        }

        foreach (var link in children.Links)
        {
            DeclineLink(LongPath.Display(link.FullName));
        }

        var offered = false;

        foreach (var version in children.Directories)
        {
            ct.ThrowIfCancellationRequested();
            offered |= CollectVersion(LongPath.Display(version.FullName));
        }

        if (offered)
        {
            Survive(versions, "After Effects' folder for its disk cache. Only this computer's cache inside it is removed.");
        }
    }

    /// <summary>
    /// One version's folder: this computer's cache, if it is there, and everything beside it named.
    /// Whether it held a cache to offer.
    /// </summary>
    private bool CollectVersion(string version)
    {
        if (FolderEntries.Of(version) is not { } entries)
        {
            Unlisted(version);
            return false;
        }

        var cache = entries.FirstOrDefault(entry => entry.Name.Equals(CacheName, StringComparison.OrdinalIgnoreCase));

        if (cache is null)
        {
            return false;
        }

        var path = LongPath.Display(cache.FullName);

        if (cache.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            DeclineLink(path);
            return false;
        }

        if (cache is not DirectoryInfo)
        {
            Survive(path, $"'{cache.Name}' is named like After Effects' disk cache but is a file, so it is left alone.");
            return false;
        }

        Caches.Add(new AfterEffectsDiskCache(path, version));
        Survive(version, $"The folder After Effects {Path.GetFileName(version)} keeps its disk cache in. Only the cache inside it is removed.");

        foreach (var entry in entries.Where(entry => entry != cache))
        {
            Survive(
                LongPath.Display(entry.FullName),
                AfterEffectsDiskCacheLayout.IsAnyCacheName(entry.Name)
                    ? $"'{entry.Name}' is the disk cache of an After Effects on another computer, which may be using it, so it is left alone."
                    : $"'{entry.Name}' sits beside After Effects' disk cache and is not part of it, so it is left alone.");
        }

        return true;
    }

    private void Survive(string path, string reason)
    {
        var unaliased = LongPath.Unaliased(path);

        if (!_temporaryFolders.Any(folder => LongPath.Contains(folder, unaliased))
            && !Survivors.Any(survivor => survivor.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Survivors.Add((path, reason));
        }
    }

    private void DeclineLink(string path)
    {
        Declined.Add(path);
        Survive(path, CacheLevelWalk.LinkReason);
        Notes.Add(CacheLevelWalk.Note(path));
    }

    private void Unreached(string path)
    {
        Notes.Add(UnreadableRoot.UnreachedNote(path));
        Survive(path, UnreadableRoot.UnreachedReason);
        Unreadable = true;
    }

    private void Unlisted(string path)
    {
        Notes.Add(UnreadableRoot.Note(path));
        Survive(path, UnreadableRoot.UnreachedReason);
        Unreadable = true;
    }
}
