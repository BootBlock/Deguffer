using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A folder Deguffer asked a tool for, and whether the tool is what named it.
///
/// <para>The flag exists for the sentence a plan says about the folder. "npm reports its cache
/// directory as X" is a claim about a subprocess, and when that subprocess failed X is the documented
/// default: a guess that a machine with a moved cache will not match. The tool's own command still
/// clears wherever the tool really keeps its cache, so on such a machine the figure describes a folder
/// the command may not touch, and only the user can tell. <see cref="GoCacheProvider"/> made the same
/// split first, for two locations at once.</para>
/// </summary>
/// <param name="Path">The folder, as the tool reported it or as documented.</param>
/// <param name="Answered">
/// False when the query did not run, exited with a failure, or printed no rooted path, in which case
/// <paramref name="Path"/> is the documented default rather than this machine's configuration.
/// </param>
internal readonly record struct AskedLocation(string Path, bool Answered)
{
    /// <summary>
    /// The last rooted line a successful query printed, or <paramref name="fallback"/>.
    ///
    /// <para>The exit code is read before the output. Whatever a failed query printed, the tool did not
    /// answer, so a rooted path in that output is never taken for one.</para>
    /// </summary>
    public static AskedLocation FromLastRootedLine(CommandOutcome outcome, string fallback)
    {
        var reported = outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(System.IO.Path.IsPathRooted)
            : null;

        return Of(reported, fallback);
    }

    /// <summary>The folder the tool named, or <paramref name="fallback"/> marked as unanswered.</summary>
    /// <param name="reported">The tool's answer, already vetted, or null where it gave none.</param>
    /// <param name="fallback">The documented default.</param>
    public static AskedLocation Of(string? reported, string fallback) =>
        reported is null ? new(fallback, Answered: false) : new(reported, Answered: true);

    /// <summary>
    /// Why a plan offers nothing when the folder is absent. A missing default says nothing about a
    /// cache the tool keeps elsewhere, so a guess is not reported as an empty cache.
    /// </summary>
    /// <param name="tool">The tool as a sentence names it, such as <c>npm</c>.</param>
    /// <param name="what">The folder as the tool's documentation names it, such as <c>cache directory</c>.</param>
    public string DescribeAbsent(string tool, string what) => Answered
        ? $"{tool} is installed but its {what} does not exist yet ({Path})."
        : $"{tool} did not say where its {what} is, and the documented default ({Path}) does not exist.";

    /// <summary>
    /// What a plan tells the user about the folder: the tool's report, or an admission that the folder
    /// is a guess that the tool's own command does not follow.
    /// </summary>
    /// <param name="tool">The tool as a sentence names it, such as <c>npm</c>.</param>
    /// <param name="what">The folder as the tool's documentation names it, such as <c>cache directory</c>.</param>
    public string Describe(string tool, string what) => Answered
        ? $"{tool} reports its {what} as {Path}."
        : $"{tool} did not say where its {what} is, so {Path} is the documented default rather than "
          + $"this machine's setting. {tool}'s own command clears wherever {tool} really keeps it, which "
          + "may not be this folder, so the figure may not describe what the command removes.";
}
