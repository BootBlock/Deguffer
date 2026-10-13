using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// One folder measured as the hard-link-aware scan measures a store, with each file's link count
/// and sizes read by the route asked for, so the two routes can be timed over the same tree.
///
/// <para>The walk runs with the values a scan ships with. What is compared is the read of each
/// file, and the walk under it is the same for both.</para>
/// </summary>
internal static class LinksRoute
{
    /// <returns>
    /// The files read, and complete where the folder was listed. A file refused by name and then
    /// read through a handle is counted once.
    /// </returns>
    public static RunTally Run(string folder, LinkRead readBy, CancellationToken ct)
    {
        long files = 0;

        var information = new FileInformation(
            (path, use) =>
            {
                Interlocked.Increment(ref files);
                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity,
            readByName: (string path, out FileInformation.FileStandardInfo info) =>
            {
                if (readBy is LinkRead.Handle)
                {
                    // As a file system that does not answer by name: the scan turns to a handle for
                    // every file once it has been told so.
                    info = default;
                    return ByNameReading.Unsupported;
                }

                var reading = StatByName.Read(path, out info);

                if (reading is ByNameReading.Read or ByNameReading.Gone)
                {
                    Interlocked.Increment(ref files);
                }

                return reading;
            });

        var result = new HardLinkAwareScanner(ScanTuner.Shipped, information)
            .MeasureAsync(folder, MinimumAge.Off, progress: null, ct: ct)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return new RunTally(Interlocked.Read(ref files), BytesRead: 0, Complete: result.Root is RootReach.Reached);
    }
}
