using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every workspace's folder in Nx's per-user folder, <c>%USERPROFILE%\.nx</c>, and what is in each.
///
/// <para><b>The layout, from Nx's source.</b> From Nx 23.2, a workspace with an identity keeps its
/// cache in <c>~\.nx\&lt;id&gt;\cache</c> and its index in <c>~\.nx\&lt;id&gt;\databases</c>, shared by
/// every checkout of the workspace (<c>packages/nx/src/utils/cache-directory.ts</c>). The id is the
/// first 16 hexadecimal characters of the SHA-256 of the workspace's identity, which is its Nx Cloud
/// id or a key from its git remote. Nx makes only those two folders in it, and nothing else in
/// <c>~\.nx</c> is examined: a folder not named that way is not one Nx made for a workspace.</para>
///
/// <para><b>The id cannot be traced to a workspace,</b> because the hash cannot be reversed and a
/// workspace that has been deleted names nothing to hash. So each folder is offered as itself, and the
/// most valuable ones are exactly those: the caches of workspaces that no longer exist, which no Nx
/// will ever trim again.</para>
/// </summary>
internal static partial class NxSharedCaches
{
    /// <summary>The folder <c>establishUserRoot</c> makes beside the cache, holding its databases.</summary>
    public const string IndexName = "databases";

    /// <summary>Nx's per-user folder, <c>NX_HOME_TMP_DIR</c>.</summary>
    public static string Root(IUserEnvironment environment) => Path.Combine(environment.UserProfile, ".nx");

    /// <summary>The workspace id a cache folder in <c>~\.nx</c> belongs to.</summary>
    public static string WorkspaceOf(string cache) => Path.GetFileName(Path.GetDirectoryName(cache)!);

    /// <summary>Every workspace folder in <paramref name="root"/>, examined.</summary>
    public static NxSharedCacheExamination Examine(string root, CancellationToken ct)
    {
        var examination = new NxSharedCacheExamination();

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            examination.Notes.Add(UnreadableRoot.Note(root));
            examination.Unreadable = true;
            return examination;
        }

        foreach (var link in scan.Links)
        {
            examination.Decline(LongPath.Display(link.FullName), CacheLevelWalk.LinkReason);
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var folder = LongPath.Display(child.FullName);

            if (!WorkspaceId().IsMatch(child.Name))
            {
                examination.Kept.Add((folder, "Not a folder Nx keeps one workspace's cache in, so it is left alone."));
                continue;
            }

            var verdict = NxCacheFolder.Examine(Path.Combine(folder, NxCacheFolder.Name), Path.Combine(folder, IndexName));

            if (verdict.Declined is { } declined)
            {
                examination.Decline(declined.Path, declined.Reason);
            }

            if (verdict.Folder is not { } cache)
            {
                continue;
            }

            examination.Caches.Add(cache);

            if (FolderEntries.Of(folder) is not { } entries)
            {
                examination.Notes.Add(UnreadableRoot.Note(folder));
                examination.Unreadable = true;
                continue;
            }

            examination.Kept.AddRange(entries
                .Where(entry => !entry.Name.Equals(NxCacheFolder.Name, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (Path.Combine(folder, entry.Name), entry.Name.Equals(IndexName, StringComparison.OrdinalIgnoreCase)
                    ? "The index of this workspace's cache, which holds its task history as well. The Nx that reads it treats a removed entry as a miss."
                    : "Not part of Nx's cache, so it is left alone.")));
        }

        return examination;
    }

    /// <summary>
    /// The folder name <c>sharedDirName</c> gives: <c>createHash('sha256')...digest('hex').substring(0, 16)</c>,
    /// which is lower case.
    /// </summary>
    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceId();
}

/// <summary>What <see cref="NxSharedCaches.Examine"/> found in <c>~\.nx</c>.</summary>
internal sealed class NxSharedCacheExamination
{
    /// <summary>The caches recognised, before the in-use check.</summary>
    public List<NxCacheFolder> Caches { get; } = [];

    /// <summary>Folders that are Nx's or look it and are left alone, with why. Each is a note too.</summary>
    public List<(string Path, string Reason)> Declined { get; } = [];

    /// <summary>Everything beside a recognised cache, and every folder in <c>~\.nx</c> Nx did not name.</summary>
    public List<(string Path, string Reason)> Kept { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    /// <summary>Whether a folder that mattered could not be listed, so the figures leave something out.</summary>
    public bool Unreadable { get; set; }

    public void Decline(string path, string reason)
    {
        Declined.Add((path, reason));
        Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{path}' alone: {reason}"));
    }
}
