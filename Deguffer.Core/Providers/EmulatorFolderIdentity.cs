using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A cache folder an examination will read, and whether it was named from inside its root, where the
/// way down from that root reaches it by this name.
/// </summary>
internal sealed record CollectedCacheFolder(EmulatorCacheFolder Folder, bool Inside);

/// <summary>
/// One folder is one folder however it is named, across every emulator's roots: which emulators own a
/// proven folder, and the one name a cache folder two roots read is read under.
///
/// <para>Both answer §5.3. A running emulator holds back what is in its folders, and a folder it shares
/// with another emulator, or a cache file both read, must name it among the owners whichever path
/// each reached it by.</para>
/// </summary>
internal static class EmulatorFolderIdentity
{
    /// <summary>
    /// Drops each root another layout knows by its program where nothing but a settings file of the
    /// same name proves it, then records with each root every layout that proved the same folder.
    /// </summary>
    /// <remarks>
    /// A folder an emulator knows by its program is not also read as the folder of one that nothing
    /// but a shared settings file name proves: Vita3K's <c>config.yml</c> is an older RPCS3's too. One
    /// proven by a file of its own stays an owner, so it is read, and its program holds the folder back
    /// while it runs.
    /// </remarks>
    public static void Settle(List<EmulatorRoot> roots, Func<string, ReachedFolder> reach)
    {
        var folders = roots.Select(root => reach(root.Path)).ToList();

        for (var i = roots.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            var found = roots[i];

            if (!found.Layout.IsKnownByItsProgram
                && roots.Where((other, j) =>
                        other.Layout.IsKnownByItsProgram
                        && folders[j].IsSameAs(folder)
                        && found.Layout.IsProvenOnlyByMarkersOf(other.Layout, found.Path))
                    .Any())
            {
                roots.RemoveAt(i);
                folders.RemoveAt(i);
            }
        }

        var owners = roots
            .Select((_, i) => (IReadOnlyList<EmulatorLayout>)[.. roots.Where((_, j) => folders[j].IsSameAs(folders[i])).Select(root => root.Layout)])
            .ToList();

        for (var i = 0; i < roots.Count; i++)
        {
            roots[i] = roots[i] with { Owners = owners[i] };
        }
    }

    /// <summary>
    /// Gives a folder two roots read one name, so what both recognise in it is one target that either
    /// emulator holds back while it runs. Two emulators' settings can name one cache folder, one of
    /// them through a <c>subst</c> letter or another mount, and the text of the two paths would
    /// otherwise make two targets for one file. A name from inside a root is kept, because the way
    /// down from that root reaches the folder by it.
    /// </summary>
    public static void NameEachFolderOnce(List<List<CollectedCacheFolder>> collected, Func<string, ReachedFolder> reach)
    {
        var all = collected.SelectMany(folders => folders.Select((_, index) => (Folders: folders, Index: index))).ToList();
        var reached = all.Select(entry => reach(entry.Folders[entry.Index].Folder.Path)).ToList();
        var names = new string[all.Count];

        for (var i = 0; i < all.Count; i++)
        {
            var same = Enumerable.Range(0, all.Count).Where(j => reached[j].IsSameAs(reached[i])).ToList();
            var chosen = same.FirstOrDefault(j => all[j].Folders[all[j].Index].Inside, same[0]);

            names[i] = all[chosen].Folders[all[chosen].Index].Folder.Path;
        }

        for (var i = 0; i < all.Count; i++)
        {
            var (folders, index) = all[i];
            folders[index] = folders[index] with { Folder = folders[index].Folder with { Path = names[i] } };
        }
    }
}
