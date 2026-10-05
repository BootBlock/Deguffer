using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// No product code reads a refusal as absence except where that reading was judged.
///
/// <para><b>Read from the source, because the danger compiles.</b>
/// <c>LongPath.FileExists</c> and <c>LongPath.DirectoryExists</c> are true only for
/// <c>PathPresence.Present</c>, so a path Windows will not describe reads as "not there". That is
/// right where the probe decides what a location <em>is</em>, such as a marker file, because a
/// refusal is no evidence for the classification. It is wrong where the probe decides whether a tool
/// is present, whether a settings file exists to be read, or whether a guard runs. Nothing in the
/// call can tell the two apart, so every use is listed here with how many times its file makes it,
/// and a new use fails until somebody judges it.</para>
///
/// <para>A presence probe asks <c>ProbeFile</c>, <c>ProbeDirectory</c> or <c>ProbeEntry</c> and gives
/// <c>Refused</c> its own meaning, or asks <c>DirectoryMayExist</c> or <c>FileMayExist</c>.</para>
/// </summary>
public sealed partial class PresenceProbeSweepTests
{
    /// <summary>
    /// Every judged use, with how many times the file makes it.
    ///
    /// <list type="bullet">
    /// <item>Classification: the file or folder says what a location is, and a refusal leaves it
    /// unrecognised and left alone. Chromium's <c>Local State</c> and profile file, Jellyfin's marker
    /// and cache tag, LM Studio's deletion marker, Squirrel's <c>Update.exe</c>, Steam's and vcpkg's
    /// root markers, the editor's identifying files, and the build-directory and <c>obj</c>
    /// signatures.</item>
    /// <item>Behind a refusal check: an earlier probe or obstacle walk stops on a refusal, so only
    /// absence reaches the call. The Claude Code providers, Epic's <c>Saved</c> folder, a Firefox
    /// profile's local half, Steam's shader container and Capture One's volume root.</item>
    /// <item>A refusal costs a recomputation: Deguffer's own scan estimate and refusal record
    /// files.</item>
    /// </list>
    /// </summary>
    private static readonly SortedDictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        ["Deguffer.Core/Execution/RefusalRecord.cs"] = 1,
        ["Deguffer.Core/Providers/CaptureOneExamination.cs"] = 1,
        ["Deguffer.Core/Providers/ChromiumUserDataDiscovery.cs"] = 2,
        ["Deguffer.Core/Providers/ClaudeCodeConversationProvider.cs"] = 1,
        ["Deguffer.Core/Providers/ClaudeCodeDerivedStateProvider.cs"] = 1,
        ["Deguffer.Core/Providers/ClaudeCodeFileHistoryProvider.cs"] = 1,
        ["Deguffer.Core/Providers/ClaudeCodeMcpLogProvider.cs"] = 1,
        ["Deguffer.Core/Providers/EpicLauncherSaved.cs"] = 1,
        ["Deguffer.Core/Providers/FirefoxCacheProvider.cs"] = 1,
        ["Deguffer.Core/Providers/JellyfinServerLayout.cs"] = 2,
        ["Deguffer.Core/Providers/LmStudioRuntimeProvider.cs"] = 3,
        ["Deguffer.Core/Providers/SquirrelDiscovery.cs"] = 1,
        ["Deguffer.Core/Providers/SteamDiscovery.cs"] = 1,
        ["Deguffer.Core/Providers/SteamShaderCacheProvider.cs"] = 2,
        ["Deguffer.Core/Providers/VcpkgDiscovery.cs"] = 1,
        ["Deguffer.Core/Providers/VsCodeUserDataDiscovery.cs"] = 2,
        ["Deguffer.Core/Safety/BuildDirectorySignature.cs"] = 2,
        ["Deguffer.Core/Safety/DotNetIntermediateSignature.cs"] = 3,
        ["Deguffer.Core/Scanning/ScanEstimateCache.cs"] = 1,
    };

    [Fact]
    public void EveryTwoStateProbeInProductCodeWasJudged()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in ProductSources())
        {
            var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');

            // Comment lines are skipped, because a doc comment names these to explain them.
            var uses = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => TwoStateProbe().Matches(line).Count);

            if (uses > 0)
            {
                found[name] = uses;
            }
        }

        Assert.Equal(Allowed, found);
    }

    /// <summary>
    /// Every C# file in a product project, found by name so a project added later is covered the
    /// moment it exists. <c>LongPath</c> itself is left out, because it defines the two.
    /// </summary>
    private static IEnumerable<string> ProductSources() =>
        Directory.EnumerateDirectories(MarkdownGuide.RepositoryRoot, "Deguffer.*")
            .Where(project => Path.GetFileName(project) is { } name
                && !name.EndsWith(".Tests", StringComparison.Ordinal)
                && name != "Deguffer.Testing")
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file)
                && !Path.GetFileName(file).Equals("LongPath.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>A call or a method group, which <c>FirstOrDefault(LongPath.FileExists)</c> is.</summary>
    [GeneratedRegex(@"\bLongPath\s*\.\s*(?:FileExists|DirectoryExists)\b")]
    private static partial Regex TwoStateProbe();
}
