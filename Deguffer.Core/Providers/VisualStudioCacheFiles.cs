using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every file Visual Studio may hold open in the caches of one <c>.vs</c> folder, for the live-tree
/// check to ask Windows about.
///
/// <para><b>Read by name and level alone, never by the evidence.</b> The check is asked at the preview
/// and again at the clean, and a cache that changed in between is the likeliest to be in use: a
/// <c>DevHub.exe</c> still writing the index can leave a file in it the evidence does not allow. A
/// question built from the evidence would then name none of that index's files and answer that
/// nothing holds it, while the step planned at the preview still runs. So every entry at a cache's
/// name and level is read, under any folder at the solution and version levels, whether or not a
/// solution beside the <c>.vs</c> folder still names it.</para>
///
/// <para>A folder Windows will not list adds nothing. Removing it needs the same listing, so the step
/// fails rather than deleting anything nobody asked about.</para>
/// </summary>
internal static class VisualStudioCacheFiles
{
    public static IReadOnlyList<string> Of(string vs, CancellationToken ct = default)
    {
        var files = new List<string>();

        Read(vs, VisualStudioLevel.Folder);

        return files;

        void Read(string directory, VisualStudioLevel level)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var entry in FolderEntries.Of(directory) ?? [])
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var path = Path.Combine(directory, entry.Name);
                var kind = entry is DirectoryInfo ? TargetKind.Directory : TargetKind.File;

                if (VisualStudioCacheLayout.CacheNamed(level, entry.Name, kind) is not null)
                {
                    Collect(path, kind);
                }
                else if (kind == TargetKind.Directory && level != VisualStudioLevel.Version)
                {
                    Read(path, level + 1);
                }
            }
        }

        void Collect(string path, TargetKind kind)
        {
            ct.ThrowIfCancellationRequested();

            if (kind == TargetKind.File)
            {
                files.Add(path);
                files.AddRange(SqliteDatabaseSignature.CompanionsOf(path));
                return;
            }

            foreach (var entry in FolderEntries.Of(path) ?? [])
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var inner = Path.Combine(path, entry.Name);

                if (entry is DirectoryInfo)
                {
                    Collect(inner, TargetKind.Directory);
                }
                else
                {
                    files.Add(inner);
                }
            }
        }
    }
}
