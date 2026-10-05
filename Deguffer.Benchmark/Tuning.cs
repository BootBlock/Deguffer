using System.Globalization;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The values a route was measured with, stated in every result.
///
/// <para>Two results are only comparable at the same values, and a value fixed in the build can
/// change between builds, so a result from an older build would otherwise be indistinguishable from
/// one taken after a value changed. A value that becomes a setting becomes an argument here, and is
/// stated the same way.</para>
/// </summary>
internal static class Tuning
{
    public static string Describe(Route route, WalkTuning walk, TableTuning table) => route.ReadsTable()
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{table.ReadBytes / 1024:N0} KiB per read, one read at a time, parsed on one thread")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{walk.Threads} folders listed at once, {walk.ListingBufferBytes / 1024} KiB listing buffer");
}
