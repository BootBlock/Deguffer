using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// No work the App queues on the dispatcher can fault without a record.
///
/// <para><b>Read from the source, because the danger compiles.</b> An exception that escapes a
/// <c>DispatcherQueue.TryEnqueue</c> callback or a <c>DispatcherQueueTimer</c> tick ends the process
/// without reaching any managed handler, so <c>crash.log</c> stays empty. The App routes each of them
/// through <c>DispatcherFaults</c>, and a raw use anywhere else fails here until it is routed or
/// judged.</para>
/// </summary>
public sealed partial class DispatcherCallbackSweepTests
{
    /// <summary>
    /// Every judged raw use, with how many times the file makes it.
    ///
    /// <list type="bullet">
    /// <item>The guard itself, which queues and ticks on behalf of everything else.</item>
    /// <item>The completion countdown's tick, which belongs to a XAML <c>DispatcherTimer</c>: a
    /// framework event, whose fault reaches <c>Application.UnhandledException</c>.</item>
    /// </list>
    /// </summary>
    private static readonly SortedDictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Shell/ContentDialogCompletionCountdown.cs"] = 1,
        ["Deguffer.App/Shell/DispatcherFaults.cs"] = 3,
    };

    [Fact]
    public void EveryDispatcherCallbackInTheAppIsGuarded()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var app = Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App");

        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file)))
        {
            var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');

            // Comment lines are skipped, because a doc comment names these to explain them.
            var uses = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => RawCallback().Matches(line).Count);

            if (uses > 0)
            {
                found[name] = uses;
            }
        }

        Assert.Equal(Allowed, found);
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.\s*TryEnqueue\s*\(|\.\s*Tick\s*\+=")]
    private static partial Regex RawCallback();
}
