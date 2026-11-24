using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Segoe Fluent Icons glyph beside a clean's safety check heading: a tick for a pass, a warning
/// for a check that could not be completed, and a cross for a failure. Three shapes rather than one
/// shape in three colours, because colour is never the only cue (§6.5): the outcome has to read on a
/// flat background, in high contrast, and to a reader who cannot tell red from green.
/// </summary>
public static class SafetyCheckGlyph
{
    public const string Passed = "\uE930";
    public const string Incomplete = "\uE7BA";
    public const string Failed = "\uEA39";

    /// <summary>
    /// The glyph for <paramref name="verdict"/>. An unnamed verdict throws rather than taking the tick,
    /// so a verdict added later cannot be drawn as a pass by default.
    /// </summary>
    public static string Of(RunVerdict verdict) =>
        verdict switch
        {
            RunVerdict.AllSurvived => Passed,
            RunVerdict.Unverified or RunVerdict.RemovedFromOutside => Incomplete,
            RunVerdict.VerificationFailed => Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
        };
}
