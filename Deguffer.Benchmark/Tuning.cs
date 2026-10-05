using System.Globalization;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The values a route was measured with, stated in every result.
///
/// <para>Two results are only comparable at the same values, and today every one of them is fixed
/// in the build, so a result from an older build would otherwise be indistinguishable from one
/// taken after a value changed. A value that becomes a setting becomes an argument here, and is
/// stated the same way.</para>
/// </summary>
internal static class Tuning
{
    public static string Describe(Route route) => route.ReadsTable()
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{MftRecordStream.RecordsPerBatch:N0} records per read, one read at a time, parsed on one thread")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{BoundedFileWalk.Parallelism} folders listed at once, listing buffer left to the runtime");
}
