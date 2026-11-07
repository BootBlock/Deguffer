using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a <see cref="TreeRemoval"/> did to the entries it handed to a delete, counted from every one
/// of its threads at once, and the outcome it adds up to once they have all ended.
/// </summary>
/// <param name="root">The removal's root, in the extended form its walk met every entry in.</param>
internal sealed class RemovalTally(string root)
{
    private readonly RefusalCounter _refused = new();
    private readonly ConcurrentDictionary<string, byte> _refusedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _stayed = new();
    private readonly string _root = root;

    private long _attempted;
    private long _reclaimed;
    private long _removed;

    /// <summary>An entry is about to be handed to a delete.</summary>
    public void Attempting() => Interlocked.Increment(ref _attempted);

    /// <summary>An entry went, and with it <paramref name="bytes"/>.</summary>
    public void Removed(long bytes)
    {
        Interlocked.Add(ref _reclaimed, bytes);
        Interlocked.Increment(ref _removed);
    }

    /// <summary>Windows would not let <paramref name="file"/> go, for <paramref name="reason"/>.</summary>
    public void Refused(WalkEntry file, RefusalReason reason)
    {
        _refused.Add(reason, file.Length);
        _refusedAt.TryAdd(EntryHolding(_root, file.FullName), 0);
    }

    /// <summary>A directory link at <paramref name="path"/> is still there.</summary>
    public void Stayed(string path) => _stayed.Enqueue(path);

    /// <summary>
    /// What the removal did, read once every thread of it has ended. Each folder is read in reverse of
    /// the walk, children before their parent, so the record comes out in one order however the threads
    /// interleaved.
    /// </summary>
    public RemovalOutcome Outcome(RemovalFolder root, RemovalBounds bounds, IFileSystem fs, bool interrupted)
    {
        var folders = root.InWalkOrder();

        // A removal stopped before it handed anything to a delete has taken nothing, and went nowhere.
        var wentIn = Interlocked.Read(ref _attempted) > 0;

        var leftStanding = new List<string>(_stayed);
        var refusedFolders = FolderRefusals.None;
        var mailStores = new List<string>();
        var spared = new List<string>();
        var userWork = new List<string>();
        var unlisted = new List<string>();
        var kept = 0;
        var removed = Interlocked.Read(ref _removed);

        for (var index = folders.Count - 1; index >= 0; index--)
        {
            var folder = (RemovalFolder)folders[index];

            // A root the caller keeps is never tried, so it is neither removed nor left standing.
            if (bounds.KeepRoot && folder.Parent is null)
            {
                continue;
            }

            if (!folder.WasTried)
            {
                // A removal stopped part-way was inside every folder it had not yet tried: it found
                // them, and any file in them may already be gone. Each is recorded as standing for that
                // reason, so §5.6 reads a protected folder among them as one this removal went into.
                // See RunResidue.
                if (wentIn)
                {
                    leftStanding.Add(folder.Path);
                }
            }
            else if (folder.Left is { } standing)
            {
                // A directory still holding something refused stays, and so does one Windows refuses
                // for itself. Neither is an error (§5.3), and both are recorded: a folder left standing
                // is where this removal went, and only the second is a refusal the reader has not
                // already been told about.
                leftStanding.Add(folder.Path);

                if (standing.Refused is { } reason && !folder.IsUnlisted)
                {
                    refusedFolders += FolderRefusals.One(reason);
                }
            }
            else
            {
                removed++;
            }
        }

        // In the order the walk met them, as the reader would list them.
        foreach (RemovalFolder folder in folders)
        {
            // Still there, whether kept as the root, never tried, or refused. One never listed and then
            // taken held nothing, because Windows removes only an empty folder.
            if (folder.IsUnlisted && !(folder.WasTried && folder.Left is null))
            {
                unlisted.Add(folder.Path);
            }

            kept += folder.Kept;
            spared.AddRange(folder.Spared);
            userWork.AddRange(folder.UserWork);
            mailStores.AddRange(folder.MailStores);
        }

        // The root is among the folders, so it has been tried by now unless the caller asked for it to
        // stay, in which case it is still there and that is the success.
        return new RemovalOutcome(
            Interlocked.Read(ref _reclaimed),
            _refused.Total,
            RootRemoved: !bounds.KeepRoot && fs.ProbeEntry(_root) is PathPresence.Absent,
            kept,
            spared.Count,
            removed)
        {
            RefusedAt = [.. _refusedAt.Keys.Select(LongPath.Display).Order(StringComparer.OrdinalIgnoreCase)],
            LeftStanding = [.. leftStanding.Select(LongPath.Display)],
            RefusedFolders = refusedFolders,
            MailStores = [.. mailStores.Select(LongPath.Display)],
            Unlisted = [.. unlisted.Select(LongPath.Display)],
            LeftAlone = [.. spared.Concat(userWork).Select(LongPath.Display)],
            UserWork = userWork.Count,
            Interrupted = interrupted,
        };
    }

    /// <summary>
    /// The entry directly inside <paramref name="root"/> that <paramref name="path"/> is at or
    /// below. Both arrive from the same walk in the same extended form, so the prefix is known to
    /// match and only the separator after it has to be found.
    /// </summary>
    private static string EntryHolding(string root, string path)
    {
        // A volume root keeps its separator, and every other root has none after it.
        var start = root.EndsWith(Path.DirectorySeparatorChar) ? root.Length : root.Length + 1;
        var end = path.IndexOf(Path.DirectorySeparatorChar, start);

        return end < 0 ? path : path[..end];
    }

}
