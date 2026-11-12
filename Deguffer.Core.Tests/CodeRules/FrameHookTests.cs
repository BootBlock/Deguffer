using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// Every handler hooked to a per-frame or per-layout event is unhooked in the same file.
///
/// <para><c>CompositionTarget.Rendering</c>, <c>LayoutUpdated</c> and the frame clock's <c>Frame</c>
/// fire for every frame or every layout pass of the whole window, so a handler left on runs for the
/// life of the process: work nobody asked for while the window idles, and a reference that keeps its
/// page alive. This is not one pattern's count, because a subscription is only sound beside its
/// matching removal, so it is held here rather than in <see cref="CodeRuleBook"/>.</para>
///
/// <para>A removal matches when it names the same event on the same owner with the same handler. A
/// lambda or anonymous method never matches, because a second one written the same way is a new
/// delegate and <c>-=</c> takes nothing off. The guard that takes a handler off just before hooking it
/// again, so it is never on twice, ends nothing, so it is not counted as the handler's removal.</para>
/// </summary>
public sealed partial class FrameHookTests
{
    internal const string BaselineName = "frame-hooks-are-unhooked";

    [Fact]
    public void EveryFrameHookIsUnhookedInItsFile()
    {
        var found = CodeRuleSweepTests.ProductFiles.Value
            .SelectMany(file => UnmatchedHooks(file.Path, SourceText.Mask(file.Text, SourceView.Code)));

        RuleBaseline.Hold(
            $"{BaselineName}. Take the handler off with -= where its work ends (on its first success, or as "
                + $"its owner unloads), and hook a named handler, never a lambda. The baseline is "
                + $"Baselines/{BaselineName}.txt.",
            found,
            Baseline.Read(BaselineName));
    }

    [Theory]
    [InlineData("tree.LayoutUpdated += Find;")]
    [InlineData("CompositionTarget.Rendering += (sender, args) => Step();")]
    [InlineData("CompositionTarget.Rendering += (s, e) => Step();\nCompositionTarget.Rendering -= (s, e) => Step();")]
    [InlineData("Rendering += delegate { Step(); };\nRendering -= delegate { Step(); };")]
    [InlineData("_clock.Frame += OnFrame;\n_other.Frame -= OnOther;")]
    [InlineData("a.LayoutUpdated += Find;\nb.LayoutUpdated -= Find;")]
    [InlineData("void Start()\n{\n    CompositionTarget.Rendering -= OnRendering;\n    CompositionTarget.Rendering += OnRendering;\n}")]
    public void AHookWithNoMatchingRemovalIsFound(string code) =>
        Assert.Single(UnmatchedHooks("Example.cs", code));

    [Theory]
    [InlineData("tree.LayoutUpdated += Find;\ntree.LayoutUpdated -= Find;")]
    [InlineData("add => CompositionTarget.Rendering += value;\nremove => CompositionTarget.Rendering -= value;")]
    [InlineData("CompositionTarget.Rendering += this.OnRendering;\nCompositionTarget.Rendering -= OnRendering;")]
    [InlineData("this._clock.Frame += OnFrame;\n_clock.Frame -= this.OnFrame;")]
    [InlineData("Stop() { CompositionTarget.Rendering -= _waiting; }\nWait() { CompositionTarget.Rendering -= _waiting;\nCompositionTarget.Rendering += _waiting; }")]
    public void AHookWithItsRemovalIsSound(string code) =>
        Assert.Empty(UnmatchedHooks("Example.cs", code));

    private static IEnumerable<RuleViolation> UnmatchedHooks(string path, string code)
    {
        var hooks = Subscription().Matches(code).Select(match => Read(match, code)).ToList();

        var removals = hooks.Where(hook => !hook.Adds && !GuardsAHook(hook, hooks, code))
            .Select(hook => hook.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var hook in hooks.Where(hook => hook.Adds && (hook.Anonymous || !removals.Contains(hook.Key))))
        {
            yield return new RuleViolation(path, hook.Line, hook.Text);
        }
    }

    /// <summary>A removal written directly before the same handler is hooked again only stops a double hook.</summary>
    private static bool GuardsAHook(Hook removal, List<Hook> hooks, string code) =>
        hooks.Any(hook => hook.Adds
            && hook.Key == removal.Key
            && hook.Start >= removal.End
            && code.AsSpan(removal.End, hook.Start - removal.End).IsWhiteSpace());

    private static Hook Read(Match match, string code)
    {
        var handler = WithoutThis(match.Groups["handler"].Value);
        var owner = WithoutThis(Whitespace().Replace(match.Groups["owner"].Value, string.Empty)).TrimEnd('.');

        return new Hook(
            Adds: match.Groups["operator"].Value == "+=",
            Key: $"{owner}.{match.Groups["event"].Value}:{Whitespace().Replace(handler, string.Empty)}",
            Anonymous: handler.Contains("=>", StringComparison.Ordinal) || handler.StartsWith("delegate", StringComparison.Ordinal),
            Start: match.Index,
            End: match.Index + match.Length,
            Line: code.AsSpan(0, match.Index).Count('\n') + 1,
            Text: match.Value.Trim());
    }

    private static string WithoutThis(string text) =>
        text.StartsWith("this.", StringComparison.Ordinal) ? text["this.".Length..] : text;

    private sealed record Hook(bool Adds, string Key, bool Anonymous, int Start, int End, int Line, string Text);

    [GeneratedRegex(@"(?<owner>\b[\w.]+\s*\.\s*)?\b(?<event>LayoutUpdated|Rendering|Frame)\s*(?<operator>\+=|-=)\s*(?<handler>[^;]+?)\s*;")]
    private static partial Regex Subscription();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
