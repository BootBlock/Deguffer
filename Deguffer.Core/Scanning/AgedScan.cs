using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One tree measured twice in one pass: all of it, and the part a removal guarded by an age would
/// take. See <see cref="IDirectoryScanner.MeasureAgedAsync"/>.
///
/// <para>Each reading is a whole <see cref="ScanResult"/> rather than a second size beside the
/// first, because the two differ in more than bytes: the folders a guard keeps standing change the
/// entry count, and only the aged reading can have withheld anything. The route, the reason and the
/// mail stores are the same in both, because one listing produced them.</para>
/// </summary>
/// <param name="Whole">The tree with no guard, as <see cref="MinimumAge.Off"/> measures it.</param>
/// <param name="Aged">The part of the tree the guard does not keep.</param>
public readonly record struct AgedScan(ScanResult Whole, ScanResult Aged)
{
    /// <summary>
    /// The guards a scanner tallies one walk under to answer for <paramref name="age"/>, in the order
    /// <see cref="Of"/> reads the readings back. The whole comes first, so a walk's progress follows it.
    /// </summary>
    internal static MinimumAge[] Keeps(MinimumAge age) => [MinimumAge.Off, age];

    /// <summary>The readings a walk tallied under <see cref="Keeps"/>, paired.</summary>
    internal static AgedScan Of(IReadOnlyList<ScanResult> readings) => new(readings[0], readings[1]);
}
