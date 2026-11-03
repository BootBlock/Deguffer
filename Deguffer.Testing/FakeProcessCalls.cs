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

        return process.OpenRefused ? ProcessOpening.Refused : ProcessOpening.Of(process.Open());
    }

    public OwnProcess Own()
    {
        OwnReads++;
        return OwnFacts;
    }
}

/// <summary>
/// One process as Windows would answer about it. Every member defaults to an answer that is read.
///
/// <para>It is read through the handles <see cref="FakeProcessCalls.Open"/> gives out, one per open,
/// and counts every read across them.</para>
/// </summary>
internal sealed class FakeProcess
{
    /// <summary>A creation time exact to the tick, as the kernel records one.</summary>
    public const long Created = 133_900_000_000_000_042;

    private readonly List<FakeProcessHandle> _handles = [];

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

    /// <summary>How many times the process was asked whether it has gone, which is how often a watch polled.</summary>
    public int ExitReads { get; private set; }

    /// <summary>Whether it was opened, and every handle given out for it has been closed.</summary>
    public bool Disposed => _handles.Count > 0 && _handles.TrueForAll(handle => handle.Closed);

    internal FakeProcessHandle Open()
    {
        var handle = new FakeProcessHandle(this);
        _handles.Add(handle);
        return handle;
    }

    internal bool? ReadExit()
    {
        ExitReads++;
        return Exited;
    }

    internal T ReadFact<T>(T fact)
    {
        FactsRead++;
        return fact;
    }
}

/// <summary>
/// One open handle to a <see cref="FakeProcess"/>. A closed one answers nothing, as Windows refuses a
/// call through a closed handle, and it stays closed when the process is opened again: so a fact read
/// through a handle after it was closed fails even where the code has opened another since.
/// </summary>
internal sealed class FakeProcessHandle(FakeProcess process) : IOpenProcess
{
    public bool Closed { get; private set; }

    public long? CreationTime() => Live().CreatedAt;

    public bool? HasExited() => Live().ReadExit();

    public uint? SessionId() => Live().ReadFact(process.Session);

    public TokenFacts Token() => Live().ReadFact(new TokenFacts(process.User, process.Integrity));

    public bool? IsCritical() => Live().ReadFact(process.Critical);

    public PackageIdentity Package() => Live().ReadFact(process.Packaging);

    public bool? IsFrozen() => Live().ReadFact(process.Frozen);

    public string? ImagePath() => Live().ReadFact(process.Image);

    public void Dispose() => Closed = true;

    private FakeProcess Live()
    {
        ObjectDisposedException.ThrowIf(Closed, this);
        return process;
    }
}
