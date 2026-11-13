using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Yarn said its <c>yarn-path</c> (Yarn 1) or <c>yarnPath</c> (Berry) setting is: the script the
/// <c>yarn</c> on the PATH hands every command to, <c>-v</c> included, which then answers in its place.
///
/// <para><b>Why it is asked beside the version.</b> The script decides every answer, but it is named
/// in an rc file, so it is no file a declaration could list in advance. Asked with <c>yarn -v</c>, it
/// is stamped with the version's inputs and every location's after it, and replacing it asks which
/// Yarn runs again
/// (<a href="https://classic.yarnpkg.com/en/docs/yarnrc">.yarnrc</a>,
/// <a href="https://yarnpkg.com/configuration/yarnrc#yarnPath">.yarnrc.yml</a>).</para>
/// </summary>
/// <param name="Answered">
/// Whether the question was answered at all. Where it was not, a script may be in use that no input
/// names, so the answers that depend on it are used for the pass and never remembered.
/// </param>
/// <param name="Script">The script as a full path, or null where none is set.</param>
internal sealed record YarnScriptSetting(bool Answered, string? Script)
{
    /// <summary>The setting of a Yarn that is neither Yarn 1 nor Berry, which neither question is put to.</summary>
    public static readonly YarnScriptSetting Unasked = new(Answered: false, Script: null);

    /// <summary>
    /// What <c>yarn config get yarn-path</c> printed. Yarn 1 resolves the setting against the folder of
    /// the rc file that holds it before printing it
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/rc.js">rc.js</a>), and prints
    /// <c>undefined</c> where it is not set.
    /// </summary>
    public static YarnScriptSetting FromClassic(CommandOutcome outcome) =>
        new(outcome.Succeeded, LongPath.Configured(outcome.LastLine));

    /// <summary>What <c>yarn config get yarnPath --json</c> printed: one JSON value, a path resolved as Berry reads it.</summary>
    public static YarnScriptSetting FromBerry(CommandOutcome outcome) =>
        new(outcome.Succeeded, YarnBerrySettings.PathIn(outcome));
}
