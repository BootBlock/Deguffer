using Deguffer.Core.Retrim;

namespace Deguffer.Testing;

/// <summary>
/// A retrim that records the volumes it was asked about and gives the answer a test set, so nothing
/// in the suite retrims the machine running it.
/// </summary>
public sealed class FakeVolumeRetrim : IVolumeRetrim
{
    /// <summary>The answer every retrim gives. Windows' success, unless a test says otherwise.</summary>
    public RetrimAnswer Answer { get; set; } = new(RetrimOutcome.Retrimmed);

    /// <summary>The volume names asked about, in the order they were asked.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Called as each retrim is asked, on the thread that asks it, so a test can look at what holds meanwhile.</summary>
    public Action? WhileRetrimming { get; set; }

    public RetrimAnswer Retrim(string volumeName)
    {
        lock (Asked)
        {
            Asked.Add(volumeName);
        }

        WhileRetrimming?.Invoke();
        return Answer;
    }
}
