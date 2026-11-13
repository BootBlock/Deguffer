using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which Yarn the <c>yarn</c> on the PATH runs, as <c>yarn -v</c> printed it from Deguffer's own folder.
/// Yarn 1's row and Berry's both ask it, through one question in the memory, so a pass and the passes
/// after it start <c>yarn -v</c> once between them.
///
/// <para><b>The version is asked, never inferred from the program's location.</b> Yarn 1 hands
/// itself off to whatever a <c>yarn-path</c> or <c>yarnPath</c> setting names before it reads any
/// command, <c>-v</c> included, and it reads those settings from <c>.yarnrc</c> and
/// <c>.yarnrc.yml</c> in the profile as well as in every parent of the working folder
/// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/index.js#L631-L662">index.js</a>,
/// <a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/util/rc.js">rc.js</a>). A Corepack
/// shim runs whichever Yarn its last known good version names. So a classic install can run
/// Yarn 4, and only asking the program from the folder its commands run in tells which.</para>
///
/// <para><c>yarn -v</c> as the first argument prints the version alone and exits, in Yarn 1
/// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/index.js#L164-L168">index.js</a>)
/// and in Berry (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/plugin-essentials/sources/commands/entries/version.ts">version.ts</a>).</para>
/// </summary>
/// <param name="Text">What <c>yarn -v</c> printed.</param>
internal sealed partial record YarnVersion(string Text)
{
    /// <summary>Whether this is Yarn 1, the only Yarn whose <c>cache clean</c> clears Yarn 1's cache.</summary>
    public bool IsClassic => ClassicVersion().IsMatch(Text);

    /// <summary>Whether this is Yarn 2 or later, the only Yarn whose settings name Berry's global folder.</summary>
    public bool IsBerry => BerryVersion().IsMatch(Text);

    /// <summary>
    /// Which Yarn <paramref name="yarn"/> runs, from <paramref name="memory"/> while nothing it reads to
    /// decide has changed. Null where it would not say.
    ///
    /// <para>Both rows declare the same inputs for it (<see cref="YarnToolInputs.Version"/>), or each
    /// would find the other's answer stale and ask again.</para>
    /// </summary>
    public static Task<YarnVersion?> AskAsync(
        ToolAnswerMemory memory, IUserEnvironment environment, IProcessRunner runner, string yarn, CancellationToken ct) =>
        memory.AskAsync(
            new ToolQuestion<YarnVersion?>(
                $"-v|{yarn}",
                environment.ToolEnvironment,
                _ => YarnToolInputs.Version(environment, yarn),
                static version => version is not null,
                askCt => RunAsync(runner, yarn, askCt)),
            ct);

    private static async Task<YarnVersion?> RunAsync(IProcessRunner runner, string yarn, CancellationToken ct) =>
        (await runner.RunAsync(yarn, "-v", ToolRun.Query, ct).ConfigureAwait(false)).LastLine is { } version
            ? new YarnVersion(version)
            : null;

    [GeneratedRegex(@"^1\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassicVersion();

    /// <summary>A release of Yarn 2 or later, as <c>yarn -v</c> prints it, release candidates included.</summary>
    [GeneratedRegex(@"^(?:[2-9]|[1-9]\d+)\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BerryVersion();
}
