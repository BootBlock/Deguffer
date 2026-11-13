using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where each tool said it keeps something, kept for as long as everything the tool read to say so
/// reads the same (#447).
///
/// <para><b>Why it outlives a pass.</b> Starting a package manager costs from a tenth of a second to
/// a second, and every preview, every rescan, every Explore scan and every duplicate search asked each
/// one again. Explore builds a fresh set of providers for each policy, so a memory kept by a provider
/// would die with it: one memory serves every provider that asks through the same runner.</para>
///
/// <para><b>Only where, never what.</b> A question about a cache's contents (a dry run, a listing)
/// changes whenever the tool is used, and nothing here could notice. Those stay in the provider's
/// <see cref="PassMemo{T}"/>.</para>
///
/// <para><b>What is never kept.</b> A failed or cancelled ask; an answer the tool did not give
/// (<see cref="ToolQuestion{T}.IsAnswer"/>); an answer whose inputs could not all be read; and an
/// answer whose inputs, or the folders holding them, were written at or after the moment the tool
/// was started, less <see cref="CoarseTimes"/>, because the tool may have read them before the
/// change. A file deleted, created by a rename or renamed away keeps no time of its own that says
/// so, and its folder's does. The next ask then reads them again, unchanged, and keeps that
/// answer.</para>
///
/// <para><b>Public only so a public provider can take one</b> (a test hands two providers the same
/// memory, as two Explore builds share <see cref="Shared"/>). Asking stays inside Core.</para>
///
/// <para><b>Two askers at once both ask.</b> Sharing one ask between them would hand one caller the
/// cancellation of another, and two Explore builds rarely overlap. Either answer is right, so the
/// later one stands.</para>
/// </summary>
public sealed class ToolAnswerMemory
{
    /// <summary>
    /// How much earlier than the tool's start an input may be written and still be taken as read: the
    /// two seconds FAT keeps a last-write time to, so a file written just after the start is never
    /// stamped as just before it.
    /// </summary>
    internal static readonly TimeSpan CoarseTimes = TimeSpan.FromSeconds(2);

    /// <summary>What the tools <see cref="ProcessRunner.Default"/> starts have said, for the whole process.</summary>
    public static readonly ToolAnswerMemory Shared = new(TimeProvider.System);

    private readonly ConcurrentDictionary<string, Remembered> _answers = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <param name="time">The clock an ask's start is read from, to compare with its inputs' times.</param>
    public ToolAnswerMemory(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// The memory for answers <paramref name="runner"/>'s tools give: <see cref="Shared"/> for the
    /// runner every provider uses unless told otherwise, and a memory of its own for any other, so a
    /// test's fake runner is never answered with what another runner heard.
    /// </summary>
    public static ToolAnswerMemory For(IProcessRunner? runner) =>
        runner is null || ReferenceEquals(runner, ProcessRunner.Default) ? Shared : new(TimeProvider.System);

    /// <summary>
    /// The remembered answer where the tool's inputs read as they did when it gave it, or the tool's
    /// answer now, which is then remembered.
    /// </summary>
    internal async Task<T> AskAsync<T>(ToolQuestion<T> question, CancellationToken ct)
    {
        if (_answers.TryGetValue(question.Key, out var held)
            && held.Answer is T answer
            && question.InputsOf(answer).Read(question.Environment).Matches(held.Fingerprint))
        {
            return answer;
        }

        return await AskAfreshAsync(question, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tool's answer now, whatever is remembered, and then remembered: for a row that deletes paths
    /// under the location it plans from, where a missed input would move a deletion (§5.2) rather
    /// than misstate a figure.
    /// </summary>
    internal async Task<T> AskAfreshAsync<T>(ToolQuestion<T> question, CancellationToken ct)
    {
        var started = (_time.GetUtcNow() - CoarseTimes).ToFileTime();
        var answer = await question.Ask(ct).ConfigureAwait(false);

        if (question.IsAnswer(answer)
            && question.InputsOf(answer) is var inputs
            && inputs.Read(question.Environment) is { IsComplete: true } read
            && read.Newest < started
            && !inputs.FoldersChangedSince(started))
        {
            _answers[question.Key] = new Remembered(answer, read);
        }
        else
        {
            _answers.TryRemove(question.Key, out _);
        }

        return answer;
    }

    /// <summary>Whether an answer to <paramref name="key"/> is held, for the tests of what is kept.</summary>
    internal bool Holds(string key) => _answers.ContainsKey(key);

    private sealed record Remembered(object? Answer, ToolFingerprint Fingerprint);
}
