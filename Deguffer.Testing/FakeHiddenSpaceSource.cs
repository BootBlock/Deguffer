using Deguffer.Core.Exploring.Hidden;

namespace Deguffer.Testing;

/// <summary>
/// What Windows states about a volume's hidden space, chosen by the test: a refusal, a figure larger
/// than the space a scan left over, or nothing at all. Records every volume it was asked about.
/// </summary>
public sealed class FakeHiddenSpaceSource : IHiddenSpaceSource
{
    /// <summary>What every read answers. Nothing stated, unless a test says otherwise.</summary>
    public HiddenSpace Answer { get; set; } = HiddenSpace.None;

    public List<string> Asked { get; } = [];

    public Task<HiddenSpace> ReadAsync(string volumeRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Asked.Add(volumeRoot);

        return Task.FromResult(Answer);
    }
}
