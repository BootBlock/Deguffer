using Deguffer.Core.Providers;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The locations the user chose to scan on their own, by provider id, so somebody who only ever
/// clears the temporary folders can scan those in seconds rather than measure every cache on the
/// machine to untick all but one row.
///
/// <para>Stored rather than held for the session, because it answers "these are the places I clean"
/// rather than "just now". It changes which locations a chosen scan measures and nothing else: a
/// full scan still measures every one, and what a plan may remove is decided by the plan.</para>
///
/// <para>A value, as <see cref="AppPreferences"/> is, so the preferences compare equal when the
/// choice has not changed. The ids are kept sorted and distinct, which is what makes two choices
/// holding the same locations equal however they were built.</para>
/// </summary>
public sealed record ProviderChoice
{
    public static readonly ProviderChoice None = new();

    private readonly IReadOnlyList<string> _ids = [];
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);

    /// <summary>
    /// The chosen providers' ids. An id no provider has any more is kept, so a location that comes
    /// back in a later version is still chosen, and simply matches nothing until then.
    /// </summary>
    public IReadOnlyList<string> Ids
    {
        get => _ids;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            // A hand-edited null or empty entry in preferences.json reaches here: the serialiser's
            // nullable checks cover the property, not the elements of the list it holds.
            _ids = [.. value.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            _chosen = new HashSet<string>(_ids, StringComparer.Ordinal);
        }
    }

    public bool Contains(ICleanupProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _chosen.Contains(provider.Id);
    }

    /// <summary>This choice with <paramref name="providers"/> added or taken out.</summary>
    public ProviderChoice With(IEnumerable<ICleanupProvider> providers, bool chosen)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var ids = providers.Select(provider => provider.Id);
        return new ProviderChoice { Ids = chosen ? [.. _ids, .. ids] : [.. _ids.Except(ids, StringComparer.Ordinal)] };
    }

    /// <summary>
    /// The chosen ones among <paramref name="providers"/>, in the order given, which is the order a
    /// full scan would measure them in.
    /// </summary>
    public IReadOnlyList<ICleanupProvider> Of(IReadOnlyList<ICleanupProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return [.. providers.Where(Contains)];
    }

    public bool Equals(ProviderChoice? other) =>
        other is not null && _ids.SequenceEqual(other._ids, StringComparer.Ordinal);

    public override int GetHashCode() => _ids.Count;
}
