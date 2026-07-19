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
    private readonly List<string> _roots;

    public SourceRootService(SourceRootStore store)
    {
        _store = store;
        _roots = [.. store.Load()];
    }

    public IReadOnlyList<string> Current => _roots;

    /// <summary>
    /// Approve <paramref name="root"/>. Returns whether it reached disk — a caller that says nothing
    /// on false is claiming an approval that will not survive a restart.
    ///
    /// Already-approved is success: the user asked for that folder to be covered, and it is.
    /// </summary>
    public bool Add(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || _roots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return Write([.. _roots, root]);
    }

    public bool Remove(string root) =>
        Write([.. _roots.Where(r => !r.Equals(root, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>
    /// Persist first, then apply. A rejected write must leave memory and disk agreeing — the
    /// alternative is a folder that is scanned for this session while the user has been told the
    /// approval was not saved, which for a setting that widens what gets deleted is the wrong way
    /// round.
    /// </summary>
    private bool Write(List<string> updated)
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
