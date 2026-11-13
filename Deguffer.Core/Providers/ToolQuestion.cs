namespace Deguffer.Core.Providers;

/// <summary>
/// One question a provider asks its tool about where it keeps something, with what the answer
/// depends on, for <see cref="ToolAnswerMemory"/>.
/// </summary>
/// <typeparam name="T">The answer.</typeparam>
/// <param name="Key">
/// What makes two questions the same one: the question and the executable asked, so a tool found at
/// another path is a new question rather than the old answer.
/// </param>
/// <param name="Environment">Every variable the tool is started with.</param>
/// <param name="InputsOf">What the tool reads to give an answer, which may name files the answer reports.</param>
/// <param name="IsAnswer">
/// Whether the tool gave an answer at all, rather than the documented default a provider falls back
/// to when the query fails. Only an answer is kept.
/// </param>
/// <param name="Ask">Starts the tool and reads its answer.</param>
internal sealed record ToolQuestion<T>(
    string Key,
    IReadOnlyDictionary<string, string> Environment,
    Func<T, ToolInputs> InputsOf,
    Func<T, bool> IsAnswer,
    Func<CancellationToken, Task<T>> Ask);
