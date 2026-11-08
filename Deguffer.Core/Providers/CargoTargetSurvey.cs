using System.Collections.Concurrent;

namespace Deguffer.Core.Providers;

/// <summary>
/// Each <c>target</c> directory's <see cref="CargoTargetLayout"/>, read once for a pass and handed to
/// both Rust rows.
///
/// <para>Shared for safety as well as for the listing it saves. The build output row empties each
/// profile around the <c>incremental</c> the incremental row offers, and that promise of one folder on
/// one row holds only while both rows divide the same reading: two readings either side of a build
/// could disagree about which profiles hold one. A reading that raced another for the same directory
/// is discarded, so every caller gets the one that was kept.</para>
/// </summary>
public sealed class CargoTargetSurvey
{
    private readonly ConcurrentDictionary<string, CargoTargetLayout> _layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The layout of <paramref name="target"/>, a recognised target directory, read once a pass.</summary>
    public CargoTargetLayout Of(string target, CancellationToken ct) =>
        _layouts.GetOrAdd(target, static (directory, token) => CargoTargetLayout.Of(directory, token), ct);

    /// <summary>Drops every reading, so the next pass reads each directory again.</summary>
    public void Invalidate() => _layouts.Clear();
}
