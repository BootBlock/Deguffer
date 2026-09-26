using System.ComponentModel;
using Deguffer.Core.Memory;

namespace Deguffer.App.Tests;

/// <summary>
/// Answers what another source answers, once the test lets it, and refuses where the test says Windows
/// would. A pick is decided off the window's thread, and what the page does with a press that beats
/// the answer, or an answer that lands after another pick, is the order this exists to arrange.
///
/// <para>A question already put is answered whatever happens to the pick meanwhile, as Windows answers
/// one: a cancellation reaches the next question, not the call in flight. That is what makes an answer
/// land after the reader has picked something else, which is the case the page has to ignore.</para>
/// </summary>
internal sealed class HeldFactSource(IProcessFactSource inner) : IProcessFactSource
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _released = new(initialState: true);
    private int _asked;
    private int _answered;

    /// <summary>How many times Windows was asked about a process, which §7.2.1 does once per pick.</summary>
    public int Asked
    {
        get
        {
            lock (_gate)
            {
                return _asked;
            }
        }
    }

    /// <summary>How many of those questions have been answered, or refused.</summary>
    public int Answered
    {
        get
        {
            lock (_gate)
            {
                return _answered;
            }
        }
    }

    /// <summary>Whether Windows refuses to answer, as it does for a process it will not open.</summary>
    public bool Refuses { get; set; }

    /// <summary>Make every question wait until <see cref="Release"/>.</summary>
    public void Hold() => _released.Reset();

    public void Release() => _released.Set();

    public ProcessFacts Read(int processId, long creationTime, CancellationToken ct)
    {
        lock (_gate)
        {
            _asked++;
        }

        _released.Wait(CancellationToken.None);

        try
        {
            return Refuses
                ? throw new Win32Exception(5)
                : inner.Read(processId, creationTime, CancellationToken.None);
        }
        finally
        {
            lock (_gate)
            {
                _answered++;
            }
        }
    }
}
