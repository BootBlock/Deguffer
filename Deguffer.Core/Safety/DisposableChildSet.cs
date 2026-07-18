namespace Deguffer.Core.Safety;

/// <summary>The classification of one child entry of a tool's root directory.</summary>
/// <param name="Name">The child's name, as it appears on disk.</param>
/// <param name="Tier">Its safety tier.</param>
/// <param name="Reason">Why it landed in that tier — shown to the user, so write it for them.</param>
public sealed record ChildClassification(string Name, SafetyTier Tier, string Reason);

/// <summary>
/// Implements §5.2: config lives next to cache, so a tool's root directory is never a target.
/// A provider declares the children it <em>recognises</em>; everything else is Tier 4 by
/// construction, which makes "we did not know what that was" fail closed rather than open.
/// </summary>
public sealed class DisposableChildSet
{
    private readonly Dictionary<string, ChildClassification> _known;

    public DisposableChildSet(IEnumerable<ChildClassification> known)
    {
        _known = known.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classify a child of the tool root. An unrecognised name is Tier 4 — never a guess,
    /// never a heuristic on the name.
    /// </summary>
    public ChildClassification Classify(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _known.TryGetValue(name, out var known)
            ? known
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                "Not a recognised disposable item for this tool, so it is left alone.");
    }

    /// <summary>Whether this child may ever appear in a cleanup plan.</summary>
    public bool IsDisposable(string name) => Classify(name).Tier.IsOfferable();
}
