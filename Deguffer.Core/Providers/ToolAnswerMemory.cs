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
/// <para><b>An answer is trusted only once its inputs read the same before the tool started and after
/// it answered.</b> A file the tool read can be edited, deleted, or moved in or away while it runs,
/// and only a reading from before the start can show that: a deleted file leaves no time to compare,
/// and a moved one keeps its old times. Nor do the folders above the inputs say it reliably, because
/// tools write in them as they answer (pnpm leaves an entry in its home folder and in the folder it
/// runs in). The inputs to read beforehand are those the last answer named, so the first answer to a
/// question is held untrusted, and the next ask confirms it only by giving the same answer: an answer
/// given from a state that came and went during the ask, or that changed while every declared input
/// stayed the same, is not one the inputs account for.</para>
///
/// <para><b>What is never trusted.</b> A failed or cancelled ask; an answer the tool did not give
/// (<see cref="ToolQuestion{T}.IsAnswer"/>); an answer whose inputs could not all be read; and an
/// answer whose inputs were written at or after the moment the tool was started, less
/// <see cref="CoarseTimes"/>, because a file system that keeps coarse times can give a write during
/// the ask the time it had before.</para>
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
    /// The trusted answer where the tool's inputs read as they did when it gave it, or the tool's answer
    /// now, which is then held (<see cref="AskAfreshAsync{T}"/>).
    /// </summary>
    internal async Task<T> AskAsync<T>(ToolQuestion<T> question, CancellationToken ct)
    {
        if (!_answers.TryGetValue(question.Key, out var held) || held.Answer is not T last)
        {
            return await AskAndHoldAsync(question, last: default, before: null, ct).ConfigureAwait(false);
        }

        // One reading serves both: what the trusted answer is checked by, and what the ask that
        // replaces it compares its own reading with.
        var now = question.InputsOf(last).Read(question.Environment);

        return held.Trusted && now.Matches(held.Fingerprint)
            ? last
            : await AskAndHoldAsync(question, last, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tool's answer now, whatever is held, and then held: trusted where the inputs the last answer
    /// named read the same before the tool started as after it answered. For a row that deletes paths
    /// under the location it plans from, where a missed input would move a deletion (§5.2) rather than
    /// misstate a figure.
    /// </summary>
    internal Task<T> AskAfreshAsync<T>(ToolQuestion<T> question, CancellationToken ct) =>
        _answers.TryGetValue(question.Key, out var held) && held.Answer is T last
            ? AskAndHoldAsync(question, last, question.InputsOf(last).Read(question.Environment), ct)
            : AskAndHoldAsync(question, last: default, before: null, ct);

    /// <param name="last">The answer held before this ask, which the new one must equal to be trusted.</param>
    /// <param name="before">What the inputs <paramref name="last"/> named read before the tool started, or null where none is held.</param>
    private async Task<T> AskAndHoldAsync<T>(ToolQuestion<T> question, T? last, ToolFingerprint? before, CancellationToken ct)
    {
        var started = (_time.GetUtcNow() - CoarseTimes).ToFileTime();
        var answer = await question.Ask(ct).ConfigureAwait(false);

        if (question.IsAnswer(answer) && question.InputsOf(answer).Read(question.Environment) is { IsComplete: true } after)
        {
            var trusted = before is not null
                && EqualityComparer<T>.Default.Equals(last, answer)
                && before.Matches(after)
                && after.Newest < started;

            _answers[question.Key] = new Remembered(answer, after, trusted);
        }
        else
        {
            _answers.TryRemove(question.Key, out _);
        }

        return answer;
    }

    /// <summary>Whether a trusted answer to <paramref name="key"/> is held, for the tests of what is kept.</summary>
    internal bool Holds(string key) => _answers.TryGetValue(key, out var held) && held.Trusted;

    /// <param name="Answer">The tool's answer.</param>
    /// <param name="Fingerprint">What its inputs read once it answered.</param>
    /// <param name="Trusted">
    /// Whether the inputs read the same before the tool started, so the answer may be served. An answer
    /// held untrusted names the inputs the next ask reads before it starts the tool.
    /// </param>
    private sealed record Remembered(object? Answer, ToolFingerprint Fingerprint, bool Trusted);
}
