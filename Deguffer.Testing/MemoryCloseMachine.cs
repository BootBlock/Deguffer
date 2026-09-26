using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.Testing;

/// <summary>
/// A machine holding one program Memory may ask to close, and every call a close is decided and
/// carried out through, all invented: the desktop's shell, Deguffer itself, and an editor with one
/// window of its own.
///
/// <para>Here rather than beside the tests that use it, because <see cref="ProcessCloser"/>'s own
/// constructor and the calls it reads through are Core's to grant, and Core grants them to this
/// library. A test of what the shell does with a close drives the real closer over this machine
/// rather than a stand-in for it, so the order the page sees — the confirmation, the report while
/// watching, the report once the program exits — is the order the closer produces.</para>
/// </summary>
internal sealed class MemoryCloseMachine
{
    public const int Shell = 100;
    public const int Own = 900;
    public const int TargetId = 4321;

    public const long TargetCreated = 5;

    private const long ShellCreated = 1;
    private const long OwnCreated = 2;

    private const nint ShellWindow = 0x0099;
    private const nint TargetWindow = 0x0011;

    public MemoryCloseMachine()
    {
        Target = new FakeProcess { ProcessId = TargetId, CreatedAt = TargetCreated };

        Processes = new FakeProcessCalls()
            .With(Target)
            .With(new FakeProcess { ProcessId = Shell, CreatedAt = ShellCreated });

        Windows = new FakeWindowCalls { Shell = ShellWindow }
            .With(new FakeWindow { Handle = ShellWindow, ProcessId = Shell })
            .With(new FakeWindow { Handle = TargetWindow, ProcessId = TargetId });

        Facts = new ProcessFactSource(Processes, Windows);
        Desktop = new FakeDesktopFacts(Shell);

        Before = Reading(withTarget: true);
        After = Reading(withTarget: false);
    }

    /// <summary>The machine as the user picks from it.</summary>
    public MemorySnapshot Before { get; }

    /// <summary>The machine once the program has gone.</summary>
    public MemorySnapshot After { get; }

    /// <summary>The program, which a test ends while the close is being watched.</summary>
    public FakeProcess Target { get; }

    public FakeProcessCalls Processes { get; }

    public FakeWindowCalls Windows { get; }

    /// <summary>What the watch waits on between its questions, which moves only when a test moves it.</summary>
    public ManualTimeProvider Clock { get; } = new();

    /// <summary>The key the program is found by in any tree built from this machine.</summary>
    public static MemoryNodeKey TargetKey => new(MemoryPart.Process, TargetId, TargetCreated);

    /// <summary>What Windows says about any process on this machine, read as §7.2.1 reads it.</summary>
    public IProcessFactSource Facts { get; }

    public IDesktopFacts Desktop { get; }

    /// <summary>§7.2.1's policy, deciding against this machine with Deguffer as <see cref="Own"/>.</summary>
    public MemoryActionPolicy Policy() => new(Facts, Desktop, Own);

    /// <summary>The closer, reading <see cref="Before"/> as it posts and <see cref="After"/> once the watch ends.</summary>
    public ProcessCloser Closer() =>
        new(Processes, Windows, Desktop, new QueuedMemorySource(Before, After), Clock, Own);

    private static MemorySnapshot Reading(bool withTarget)
    {
        var builder = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, ShellCreated)
            .Process(Own, 1, "Deguffer.exe", 100, OwnCreated);

        return withTarget ? builder.Process(TargetId, Shell, "editor.exe", 300, TargetCreated).Build() : builder.Build();
    }
}
