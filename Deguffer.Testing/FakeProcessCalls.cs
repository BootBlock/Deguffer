using Deguffer.Core.Memory;

namespace Deguffer.Testing;

/// <summary>
/// The processes a test says Windows would describe, and how each answers. It counts the opens, so a
/// test can hold §7.2.1's rule that nothing is asked of Windows for a row nobody selected.
/// </summary>
internal sealed class FakeProcessCalls : IProcessCalls
{
    /// <summary>Invented, as §"No secrets or personal data" requires of a fixture.</summary>
    public const string OwnUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public const string OtherUser = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    public const uint OwnSession = 1;
    public const uint OtherSession = 2;

    public const int Medium = 0x2000;
    public const int High = 0x3000;

    private readonly Dictionary<int, FakeProcess> _processes = [];
    private readonly List<int> _opened = [];

    public OwnProcess OwnFacts { get; set; } = new(OwnSession, OwnUser, Medium);

    /// <summary>Every identifier an open was attempted for, in order.</summary>
    public IReadOnlyList<int> Opened => _opened;

    public int OwnReads { get; private set; }

    public FakeProcessCalls With(FakeProcess process)
    {
        _processes[process.ProcessId] = process;
        return this;
    }

    public ProcessOpening Open(int processId)
    {
        _opened.Add(processId);

        // An identifier no process holds is one nothing was registered for.
        if (!_processes.TryGetValue(processId, out var process))
        {
            return ProcessOpening.NotRunning;
        }

        return process.OpenRefused ? ProcessOpening.Refused : ProcessOpening.Of(process.Opened());
    }

    public OwnProcess Own()
    {
        OwnReads++;
        return OwnFacts;
    }
}

/// <summary>One process as Windows would answer about it. Every member defaults to an answer that is read.</summary>
internal sealed class FakeProcess : IOpenProcess
{
    /// <summary>A creation time exact to the tick, as the kernel records one.</summary>
    public const long Created = 133_900_000_000_000_042;

    public required int ProcessId { get; init; }

    /// <summary>True where Windows holds the identifier but will not open it.</summary>
    public bool OpenRefused { get; init; }

    public long? CreatedAt { get; init; } = Created;

    /// <summary>
    /// Whether it has gone. Settable, because §7.2.1's watch has no deadline and a test has to be
    /// able to end the process while the watch is running rather than only before it starts.
    /// </summary>
    public bool? Exited { get; set; } = false;

    public uint? Session { get; init; } = FakeProcessCalls.OwnSession;

    public string? User { get; init; } = FakeProcessCalls.OwnUser;

    public long? Integrity { get; init; } = FakeProcessCalls.Medium;

    public bool? Critical { get; init; } = false;

    public PackageIdentity Packaging { get; init; } = PackageIdentity.NotPackaged;

    public bool? Frozen { get; init; }

    /// <summary>The program it runs, or null where Windows would not say.</summary>
    public string? Image { get; init; }

    /// <summary>How many facts beyond its identity were read, which a process that has gone must not reach.</summary>
    public int FactsRead { get; private set; }

    /// <summary>Whether the handle the latest open gave out has been closed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many times the process was asked whether it has gone, which is how often a watch polled.</summary>
    public int ExitReads { get; private set; }

    public long? CreationTime()
    {
        Live();
        return CreatedAt;
    }

    public bool? HasExited()
    {
        Live();
        ExitReads++;
        return Exited;
    }

    public uint? SessionId()
    {
        Live();
        FactsRead++;
        return Session;
    }

    public TokenFacts Token()
    {
        Live();
        FactsRead++;
        return new TokenFacts(User, Integrity);
    }

    public bool? IsCritical()
    {
        Live();
        FactsRead++;
        return Critical;
    }

    public PackageIdentity Package()
    {
        Live();
        FactsRead++;
        return Packaging;
    }

    public bool? IsFrozen()
    {
        Live();
        FactsRead++;
        return Frozen;
    }

    public string? ImagePath()
    {
        Live();
        FactsRead++;
        return Image;
    }

    public void Dispose() => Disposed = true;

    /// <summary>A new handle to the same process, as each open of it gives one.</summary>
    internal FakeProcess Opened()
    {
        Disposed = false;
        return this;
    }

    /// <summary>
    /// A closed handle answers nothing, as Windows refuses a call through one. Without this a fact
    /// read after the handle was closed would read as well as one read through it.
    /// </summary>
    private void Live() => ObjectDisposedException.ThrowIf(Disposed, this);
}
