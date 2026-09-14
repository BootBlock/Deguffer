using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// The folders the user has approved Deguffer to look for build output in, and the one place that
/// writes them back.
///
/// Deliberately separate from <see cref="PreferenceService"/>, mirroring the split in Core: these
/// are the first stored setting that changes what Deguffer will delete rather than how it looks, so
/// they do not travel with the presentation preferences.
/// </summary>
public sealed class SourceRootService
{
    private readonly SourceRootStore _store;
    private readonly List<SourceRoot> _roots;

    public SourceRootService(SourceRootStore store)
    {
        _store = store;
        _roots = [.. store.Load()];
    }

    public IReadOnlyList<SourceRoot> Current => _roots;

    /// <summary>
    /// Approve <paramref name="root"/>. Returns whether it reached disk — a caller that says nothing
    /// on false is claiming an approval that will not survive a restart.
    ///
    /// Already-approved is success: the user asked for that folder to be covered, and it is.
    ///
    /// <para><b>An already-approved folder is updated rather than ignored where the answer about its
    /// volume has changed.</b> A plan that refused a folder on a cloud mount tells the user to add it
    /// again to have it searched, and an add that quietly did nothing would leave them reading the
    /// warning, accepting it, and getting the same refusal next time.</para>
    /// </summary>
    public bool Add(SourceRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (string.IsNullOrWhiteSpace(root.Path))
        {
            return true;
        }

        var same = _roots.FindIndex(
            existing => existing.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase));

        if (same < 0)
        {
            return Write([.. _roots, root]);
        }

        if (_roots[same] == root)
        {
            return true;
        }

        // Replaced in place rather than removed and appended, so re-approving a folder does not move
        // it to the bottom of the list the user is reading.
        var updated = new List<SourceRoot>(_roots);
        updated[same] = root;

        return Write(updated);
    }

    public bool Remove(string path) =>
        Write([.. _roots.Where(root => !root.Path.Equals(path, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>
    /// Persist first, then apply. A rejected write must leave memory and disk agreeing — the
    /// alternative is a folder that is scanned for this session while the user has been told the
    /// approval was not saved, which for a setting that widens what gets deleted is the wrong way
    /// round.
    /// </summary>
    private bool Write(List<SourceRoot> updated)
    {
        // Adopt what the store actually kept, not what was handed to it. The store drops entries it
        // cannot use, and taking the requested list instead would leave Settings listing a folder
        // that is not in the file and will not be searched.
        if (!_store.Save(updated, out var stored))
        {
            return false;
        }

        _roots.Clear();
        _roots.AddRange(stored);

        return true;
    }
}
