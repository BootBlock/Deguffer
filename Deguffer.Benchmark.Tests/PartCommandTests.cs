using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The command run before each part of the race runs as typed and is waited for, and one that fails
/// stops the run rather than leaving a part measured as though it had done its work.
/// </summary>
public sealed class PartCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The file exists when the call returns, so the command ran, and ran to its end, before the part
    /// it comes before. The quotes around the path reach cmd.exe as typed.
    /// </summary>
    [Fact]
    public void RunsTheCommandAsTypedAndWaitsForIt()
    {
        var marker = Path.Combine(_temp.CreateDirectory("a folder"), "ran.txt");

        PartCommand.Run($"echo ran> \"{marker}\"");

        Assert.Equal("ran", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void ACommandThatFailsStopsTheRun()
    {
        var failed = Assert.Throws<PartCommandFailedException>(() => PartCommand.Run("exit 3"));

        Assert.Contains("exit code 3", failed.Message, StringComparison.Ordinal);
    }
}
