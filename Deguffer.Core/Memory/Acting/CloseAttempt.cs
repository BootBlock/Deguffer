namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What came of asking one program to close: the verdict at the moment of the action, and the report
/// where the action went ahead.
///
/// <para>Two outcomes rather than one, because §7.2.1 decides every refusal twice. A machine changes
/// between the row being selected and the user confirming — the program exits, its identifier passes
/// to another process, its last window closes — so the second decision can refuse what the first
/// allowed, and then there is no report to give, only a sentence saying why.</para>
/// </summary>
/// <param name="Verdict">What the policy said with the handle held, immediately before anything was posted.</param>
/// <param name="Report">The close, or null where the verdict refused it.</param>
public sealed record CloseAttempt(MemoryVerdict Verdict, CloseReport? Report);
