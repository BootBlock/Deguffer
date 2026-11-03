using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>arduino-cli cache clean</c>, run now, would still remove only the downloads folder the
/// plan examined, holding only what the CLI stages there.
///
/// <para><b>Why the plan's own look is not enough.</b> The command removes whichever folder the CLI
/// resolves as its downloads folder when it starts, outright and with everything in it
/// (<c>commands/service_cache_clean.go</c>). A setting edited after the preview sends it to a folder
/// nobody examined, and a file saved into the folder after the preview goes with the archives. §5.6
/// could notice neither, because what the plan never listed is not something it protects.</para>
///
/// <para>So the step runs only while everything the CLI reads to find the folder is what it was when
/// the CLI answered (see <see cref="ArduinoConfiguration"/>), and the folder, read again by the
/// plan's own rule, is not a link and holds only the CLI's <c>packages</c> and <c>libraries</c>. This
/// asks no subprocess.</para>
///
/// <para>And only while every other CLI the plan heard from, a CLI the user starts now among them,
/// would still read what it read when the plan was made (see <see cref="ArduinoAnswersCheck"/>),
/// because the plan declined a clean that would reach one of their folders.</para>
/// </summary>
/// <param name="readAgain">Reads the configuration afresh, as the plan first did.</param>
/// <param name="planned">What the CLI read when it answered for the plan.</param>
/// <param name="answers">Whether every CLI the plan heard from still reads what it read then.</param>
/// <param name="downloads">The downloads folder the plan examined.</param>
internal sealed class ArduinoCleanTarget(
    Func<ArduinoConfiguration> readAgain,
    ArduinoConfiguration planned,
    ArduinoAnswersCheck answers,
    string downloads) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        if (!readAgain().SameAs(planned))
        {
            return "the Arduino CLI's configuration, or a setting that moves its folders, has changed since the "
                + $"preview, so its cache clean could remove a folder other than {downloads}";
        }

        if (answers.WhyNot(downloads) is { } moved)
        {
            return moved;
        }

        return ArduinoFolderRules.WhyNotEmptied(downloads) switch
        {
            { Absent: true } => $"{downloads} is no longer there",
            { Unlisted: true } => $"Windows would not list {downloads}, so Deguffer cannot tell what the Arduino CLI's "
                + "cache clean would remove",
            { Why: { } why } => $"{downloads} changed since the preview: {LowerFirst(why)}",
            _ => null,
        };
    }

    /// <summary>The sentence as a clause, which completes "Not run: …".</summary>
    private static string LowerFirst(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..].TrimEnd('.');
}
