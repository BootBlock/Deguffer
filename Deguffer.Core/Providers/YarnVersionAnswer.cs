using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which Yarn the <c>yarn</c> on the PATH runs, and the script that Yarn's <c>yarn-path</c> or
/// <c>yarnPath</c> setting hands it to. Yarn 1's row and Berry's both ask it, through one question in
/// the memory, so a pass and the passes after it start <c>yarn -v</c> once between them.
///
/// <para><b>Why the script is part of the answer.</b> The script answers <c>-v</c> in the program's
/// place (<see cref="YarnVersion"/>), so copying another release over it changes the version while
/// every rc file reads the same. Declared with the version's inputs, it asks again. Which setting
/// names it depends on which Yarn read it, so both are asked beside <c>-v</c>, and the one the
/// reported version answers is used.</para>
///
/// <para><b>A Berry answer is not kept while a Yarn 1 rc file could have handed Yarn on.</b> Yarn 1
/// reads <c>yarn-path</c> from its own rc files and runs the script it names, and when that script is
/// a Berry, Berry reports only the <c>yarnPath</c> its own <c>.yarnrc.yml</c> holds. Nothing then
/// names the script that answered, so nothing could notice it replaced.</para>
/// </summary>
/// <param name="Version">What <c>yarn -v</c> printed, or null where it would not say.</param>
/// <param name="Script">
/// What the reported Yarn said its script setting is, or <see cref="YarnScriptSetting.Unasked"/> for
/// a Yarn that is neither Yarn 1 nor Berry.
/// </param>
/// <param name="MayHaveBeenHandedOn">
/// Whether Berry answered while a Yarn 1 rc file was there that could have handed Yarn to it
/// (<see cref="YarnToolInputs.MayHandYarnOnUnreported"/>), so the script that answered may be one
/// nothing names.
/// </param>
internal sealed record YarnVersionAnswer(YarnVersion? Version, YarnScriptSetting Script, bool MayHaveBeenHandedOn)
{
    /// <summary>
    /// Whether Yarn said which it is and which script it runs, so the answer and everything it depends
    /// on are known. Anything less is used for the pass and never remembered, and nor is any answer
    /// that depends on it.
    /// </summary>
    public bool IsAnswer => Version is not null && Script.Answered && !MayHaveBeenHandedOn;

    /// <summary>
    /// Which Yarn <paramref name="yarn"/> runs, from <paramref name="memory"/> while nothing it reads to
    /// decide has changed. For what a row declares, and for a plan that only runs Yarn's own command.
    /// </summary>
    public static Task<YarnVersionAnswer> AskAsync(
        ToolAnswerMemory memory, IUserEnvironment environment, IProcessRunner runner, string yarn, CancellationToken ct) =>
        memory.AskAsync(Question(environment, runner, yarn), ct);

    /// <summary>
    /// Which Yarn <paramref name="yarn"/> runs, from Yarn now whatever <paramref name="memory"/> holds:
    /// for a plan that deletes by path under a folder that Yarn's settings name.
    /// </summary>
    public static Task<YarnVersionAnswer> AskAfreshAsync(
        ToolAnswerMemory memory, IUserEnvironment environment, IProcessRunner runner, string yarn, CancellationToken ct) =>
        memory.AskAfreshAsync(Question(environment, runner, yarn), ct);

    /// <summary>
    /// The one question both rows ask. Both declare the same inputs for it
    /// (<see cref="YarnToolInputs.Version"/>), or each would find the other's answer stale and ask again.
    /// </summary>
    private static ToolQuestion<YarnVersionAnswer> Question(IUserEnvironment environment, IProcessRunner runner, string yarn) =>
        new(
            $"-v;config get yarn-path;config get yarnPath --json|{yarn}",
            environment.ToolEnvironment,
            answer => YarnToolInputs.Version(environment, yarn, answer.Script),
            static answer => answer.IsAnswer,
            askCt => RunAsync(runner, environment, yarn, askCt));

    /// <summary>
    /// <c>yarn -v</c> and both script settings, started together so a miss costs one round of waiting.
    /// Yarn 1 prints <c>yarn-path</c> (<see cref="YarnScriptSetting.FromClassic"/>) and Berry fails it;
    /// Berry prints <c>yarnPath</c> (<see cref="YarnScriptSetting.FromBerry"/>).
    /// </summary>
    private static async Task<YarnVersionAnswer> RunAsync(IProcessRunner runner, IUserEnvironment environment, string yarn, CancellationToken ct)
    {
        var printed = runner.RunAsync(yarn, "-v", ToolRun.Query, ct);
        var classic = runner.RunAsync(yarn, "config get yarn-path", ToolRun.Query, ct);
        var berry = runner.RunAsync(yarn, "config get yarnPath --json", ToolRun.Query, ct);

        // All three are awaited together, so a fault in one never leaves another unobserved.
        await Task.WhenAll(printed, classic, berry).ConfigureAwait(false);

        var version = (await printed.ConfigureAwait(false)).LastLine is { } text ? new YarnVersion(text) : null;

        var script = version switch
        {
            { IsClassic: true } => YarnScriptSetting.FromClassic(await classic.ConfigureAwait(false)),
            { IsBerry: true } => YarnScriptSetting.FromBerry(await berry.ConfigureAwait(false)),
            _ => YarnScriptSetting.Unasked,
        };

        // Decided once, as Yarn answers, so every question that depends on which Yarn ran reads the same.
        return new YarnVersionAnswer(version, script, version is { IsBerry: true } && YarnToolInputs.MayHandYarnOnUnreported(environment));
    }
}
