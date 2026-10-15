using Deguffer.Core.Execution;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Choosing;

/// <summary>
/// Which providers the last scan that measured them found not installed, so the list of what to scan
/// can say so beside them. Somebody choosing what to scan gains nothing from ticking a launcher this
/// machine does not have, and cannot know that without being told.
///
/// <para>Read off <see cref="FindingStatus.ToolchainMissing"/>, the state the row itself reports as
/// "Not installed", so the list and the row cannot disagree. A provider waiting on a source folder
/// is not missing: the user can do something about that one.</para>
///
/// <para>It only informs. A provider marked here can still be chosen, because a tool installed since
/// that scan is exactly what a fresh scan finds. Each scan overwrites what it measured and leaves the
/// rest, so a chosen scan does not forget what an earlier full one found. Held for the session: a
/// verdict from a scan days ago says little about the machine now.</para>
/// </summary>
public sealed class MissingProviders
{
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

    /// <summary>Raised when a provider's mark changes.</summary>
    public event EventHandler<ICleanupProvider>? Changed;

    public void Record(ICleanupProvider provider, FindingStatus status)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var changed = status == FindingStatus.ToolchainMissing
            ? _missing.Add(provider.Id)
            : _missing.Remove(provider.Id);

        if (changed)
        {
            Changed?.Invoke(this, provider);
        }
    }

    public bool WasMissing(ICleanupProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _missing.Contains(provider.Id);
    }
}
