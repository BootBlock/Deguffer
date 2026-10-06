using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The guard anchored at the evidence against the stamps the file system really writes. Nothing here
/// can be faked: the claim is about how the clock this process reads relates to the clock NTFS stamps
/// a write with.
/// </summary>
public sealed class MinimumAgeStampTests : IDisposable
{
    /// <summary>
    /// Enough writes that a cut-off at the instant itself fails this on every run: the clock was ahead
    /// of the stamp of a third or more of the writes made straight after it was read.
    /// </summary>
    private const int Writes = 500;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// An editor rewriting a handshake file the moment after the evidence was read. The file already
    /// exists and is old, so its creation time does not move and only the write's own stamp can keep it.
    /// </summary>
    [Fact]
    public void AFileRewrittenAfterTheEvidenceWasReadIsKeptHoweverSoonAfter()
    {
        var path = TempDirectory.Age(_temp.CreateFile(16, "51234.lock"), TimeSpan.FromDays(1));

        for (var write = 0; write < Writes; write++)
        {
            var guard = MinimumAge.Since(DateTime.UtcNow);

            File.WriteAllText(path, $"{write}");

            Assert.True(
                guard.ProtectsFile(path),
                $"write {write} was stamped {File.GetLastWriteTimeUtc(path):O}, before the cut-off {guard.KeepFromUtc:O}");
        }
    }
}
