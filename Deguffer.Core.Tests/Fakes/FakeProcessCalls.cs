using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

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
    public const int MediumUiAccess = 0x2010;
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

        if (!_processes.TryGetValue(processId, out var process))
        {
            return ProcessOpening.NotRunning;
        }

        return process.Outcome switch
        {
            OpenOutcome.Opened => ProcessOpening.Of(process),
            OpenOutcome.NotRunning => ProcessOpening.NotRunning,
            _ => ProcessOpening.Refused,
        };
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

    public OpenOutcome Outcome { get; init; } = OpenOutcome.Opened;

    public long? CreatedAt { get; init; } = Created;

    public bool? Exited { get; init; } = false;

    public uint? Session { get; init; } = FakeProcessCalls.OwnSession;

    public string? User { get; init; } = FakeProcessCalls.OwnUser;

    public int? Integrity { get; init; } = FakeProcessCalls.Medium;

    public bool? Critical { get; init; } = false;

    public PackageIdentity Packaging { get; init; } = PackageIdentity.NotPackaged;

    public bool? Frozen { get; init; }

    /// <summary>How many facts beyond its identity were read, which a process that has gone must not reach.</summary>
    public int FactsRead { get; private set; }

    public bool Disposed { get; private set; }

    public long? CreationTime() => CreatedAt;

    public bool? HasExited() => Exited;

    public uint? SessionId()
    {
        FactsRead++;
        return Session;
    }

    public TokenFacts Token()
    {
        FactsRead++;
        return new TokenFacts(User, Integrity);
    }

    public bool? IsCritical()
    {
        FactsRead++;
        return Critical;
    }

    public PackageIdentity Package()
    {
        FactsRead++;
        return Packaging;
    }

    public bool? IsFrozen()
    {
        FactsRead++;
        return Frozen;
    }

    public void Dispose() => Disposed = true;
}
