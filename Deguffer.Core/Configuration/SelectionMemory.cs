using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// What the Storage page's rows and steps start ticked as: the user's remembered choice where
/// there is one, the §3 tier default where there is not.
///
/// <para>The rule lives here rather than in the shell so it is provable without a window. What it
/// must never do is turn a remembered tick into a pre-selection the tier model forbids. §7 is
/// explicit that with both confirmations switched off, what stands between the user and an
/// irreversible deletion is the preview and Tier 3 never being pre-selected — and a tick restored
/// from a previous session is a pre-selection, whoever originally made it. So Tier 3 is offered
/// unticked every time, and this class is where that is enforced.</para>
///
/// <para>Mutable, because it is the live state of a page the user is clicking on. Writing it to
/// disk belongs to <see cref="SelectionStore"/>.</para>
/// </summary>
public sealed class SelectionMemory
{
    private readonly Dictionary<string, RememberedSelection> _byProvider = new(StringComparer.Ordinal);

    public SelectionMemory(IReadOnlyDictionary<string, RememberedSelection> remembered)
    {
        ArgumentNullException.ThrowIfNull(remembered);

        foreach (var (providerId, selection) in remembered)
        {
            _byProvider[providerId] = selection with { Steps = KeyedForLookup(selection.Steps) };
        }
    }

    /// <summary>
    /// Everything remembered as it is now, for a write on another thread while the user goes on
    /// ticking.
    ///
    /// <para>A shallow copy is enough: <see cref="Remember"/> replaces a provider's record whole, with a
    /// step map built for it and never changed after, so nothing the copy refers to changes under
    /// the write.</para>
    /// </summary>
    public IReadOnlyDictionary<string, RememberedSelection> Snapshot() =>
        new Dictionary<string, RememberedSelection>(_byProvider, StringComparer.Ordinal);

    /// <summary>Whether the row for <paramref name="provider"/> starts ticked.</summary>
    /// <param name="byDefault">
    /// §3's "Default" column for this finding, which is the answer whenever nothing is remembered.
    /// </param>
    public bool RowStartsSelected(ICleanupProvider provider, bool byDefault) =>
        Permitted(
            provider.Tier,
            _byProvider.TryGetValue(provider.Id, out var remembered) ? remembered.IsSelected : byDefault);

    /// <summary>Whether one step of that row starts ticked.</summary>
    /// <param name="rowStartsSelected">
    /// What <see cref="RowStartsSelected"/> answered for the row this step belongs to. A step this
    /// memory has not seen follows the row, but only as far as §3's default for the provider allows.
    ///
    /// Both halves are needed, and each catches a case the other misses. Without the row, a
    /// workspace cloned since the last scan would arrive ticked inside a row the user had cleared.
    /// Without the default, it would arrive ticked inside a Tier 2 row the user had ticked — and §3
    /// offers Tier 2 without pre-selecting it, which applies to a directory nobody has seen yet
    /// exactly as it does on a first run. A researched provider's folder is held back for the same
    /// reason (<see cref="ProviderBasis"/>). Ticking the row is an answer about the workspaces that
    /// were in it, not a standing instruction about every one that appears later.
    /// </param>
    public bool StepStartsSelected(ICleanupProvider provider, string stepKey, bool rowStartsSelected) =>
        Permitted(
            provider.Tier,
            _byProvider.TryGetValue(provider.Id, out var remembered)
            && remembered.Steps.TryGetValue(stepKey, out var selected)
                ? selected
                : rowStartsSelected && provider.IsPreSelectedByDefault());

    /// <summary>
    /// Record what one row is ticked as now, replacing whatever was remembered about it.
    ///
    /// Wholesale rather than merged: the caller holds the row, so its steps are the complete and
    /// current set, and merging would keep an entry for every path the provider has ever planned.
    /// </summary>
    public void Remember(string providerId, RememberedSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        _byProvider[providerId] = selection with { Steps = KeyedForLookup(selection.Steps) };
    }

    /// <summary>
    /// §3's two absolutes, which no remembered answer may move: Tier 4 is excluded from the UI
    /// entirely, and Tier 3 is shown and never pre-selected. §7 leans on the second as what stands
    /// between the user and an irreversible deletion once both confirmations are switched off.
    ///
    /// Applied to the remembered answer and to the default alike, so each rule has one place to
    /// hold rather than two that have to agree. Tier 4 reaches no row today, because no provider
    /// declares it and §5.2's unrecognised children are refused inside the provider long before a
    /// step exists. It is stated because this class is a new route to a ticked row, and the code it
    /// replaced could not produce one for either tier.
    /// </summary>
    private static bool Permitted(SafetyTier tier, bool selected) =>
        selected && tier.IsOfferable() && !tier.IsIrreversibleLoss();

    /// <summary>
    /// Most step keys are paths, and NTFS does not distinguish their case. A scan reporting
    /// <c>...\node_modules</c> where the last one reported <c>...\Node_Modules</c> is describing the
    /// same directory, and treating those as two steps would quietly restore one the user had
    /// unticked.
    ///
    /// Assigned through the indexer rather than built with <c>ToDictionary</c>, because a
    /// hand-edited file may hold two keys that differ only in case, and that is not worth throwing
    /// the whole memory away over.
    /// </summary>
    private static Dictionary<string, bool> KeyedForLookup(IReadOnlyDictionary<string, bool> steps)
    {
        Dictionary<string, bool> keyed = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, selected) in steps)
        {
            keyed[key] = selected;
        }

        return keyed;
    }
}
