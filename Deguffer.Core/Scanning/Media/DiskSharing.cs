namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// Puts volumes into sets by the physical disks they are on (<see cref="VolumeMedia.PhysicalDisks"/>),
/// each set every volume that shares a disk with another in it.
///
/// <para>Two volumes on one disk share its heads, so work on both at once divides one disk's speed
/// between them, and on a spinning disk the seeks between them make the pair much slower than either
/// alone. Sets on different disks can be worked at the same time.</para>
///
/// <para>What to do with a volume whose disks are not listed is each caller's decision, because it
/// depends on what the work costs a disk it was not meant to share. Here such a volume is a set of
/// its own.</para>
/// </summary>
internal static class DiskSharing
{
    /// <param name="disksOf">The disks one volume is on, or none where they are not listed.</param>
    /// <returns>
    /// Each set in the order of its first volume in <paramref name="volumes"/>, and the volumes in each
    /// in that order too.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<T>> Sets<T>(IReadOnlyList<T> volumes, Func<T, IReadOnlyList<int>> disksOf)
    {
        var setOf = Enumerable.Range(0, volumes.Count).ToArray();
        Dictionary<int, int> firstOnDisk = [];

        // Joined under the smaller index, so each set is named by its first volume and the sets come
        // out in the order the volumes were given.
        int Find(int volume) => setOf[volume] == volume ? volume : setOf[volume] = Find(setOf[volume]);

        void Join(int one, int other)
        {
            var (a, b) = (Find(one), Find(other));
            setOf[Math.Max(a, b)] = Math.Min(a, b);
        }

        for (var i = 0; i < volumes.Count; i++)
        {
            foreach (var disk in disksOf(volumes[i]))
            {
                if (firstOnDisk.TryGetValue(disk, out var other))
                {
                    Join(i, other);
                }
                else
                {
                    firstOnDisk[disk] = i;
                }
            }
        }

        return
        [
            .. Enumerable.Range(0, volumes.Count)
                .GroupBy(Find)
                .Select(set => (IReadOnlyList<T>)[.. set.Select(i => volumes[i])]),
        ];
    }
}
