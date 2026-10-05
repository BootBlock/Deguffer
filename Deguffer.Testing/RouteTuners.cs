using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Testing;

/// <summary>
/// A <see cref="ScanTuner"/> at the shipped values on the route a test states, with every drive of
/// unknown kind, as <see cref="ScanTuner.Shipped"/> has.
///
/// <para>Under Auto a question races the walk against the table, so which answers depends on which
/// finishes first, and a test whose fixture table describes a real folder cannot know. A test that
/// asserts the table's answer asks for <see cref="Table"/>, where the table is waited for.</para>
/// </summary>
public static class RouteTuners
{
    /// <summary>The table wherever it can be read, waited for.</summary>
    public static ScanTuner Table { get; } = With(ScanRoute.Table);

    private static ScanTuner With(ScanRoute route) => new(
        new FakePreferences(AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = route } }),
        new VolumeMediaCache(new FakeStorageQueries()),
        new FakeVolumeInventory());
}
