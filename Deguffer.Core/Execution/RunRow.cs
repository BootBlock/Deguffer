using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// One location's line in a finished clean's result: its name, what it removed, and each thing it left
/// behind with the reason.
///
/// <para>A line per location, never one total, because the total cannot answer the question a clean of
/// several locations raises: did the large one work? Eight rows that freed 20 GB between them say
/// nothing about whether the one that promised 18 GB freed any of it.</para>
///
/// <para>What was left is worded by <see cref="LeftInPlace"/>, the words each step's own message uses,
/// so a location's line and the step that ran it never describe the same refusal two ways.</para>
/// </summary>
/// <param name="Name">The location, as its row on the page names it.</param>
/// <param name="Removed">
/// What went: the bytes, or the entries where no bytes went, beside what a sync app was asked to release
/// and what a program will remove when it next starts. Kept apart for the reason <see cref="RunFigures"/>
/// keeps them apart (§5.4).
/// </param>
/// <param name="LeftBehind">A sentence for each thing the location left, or none.</param>
public sealed record RunRow(string Name, string Removed, IReadOnlyList<string> LeftBehind)
{
    /// <summary>The whole line as one passage, for a screen reader and for anything that copies it.</summary>
    public string Line => LeftBehind.Count == 0
        ? $"{Name}: {Removed}."
        : $"{Name}: {Removed}. {string.Join(" ", LeftBehind)}";

    /// <summary>Whether the location left anything behind, whatever the reason.</summary>
    public bool LeftSomething => LeftBehind.Count > 0;

    public static RunRow Of(CleanupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RunRow(result.ProviderName, RemovedBy(result), LeftBy(result));
    }

    private static string RemovedBy(CleanupResult result)
    {
        var parts = new List<string>(3);

        if (result.BytesReclaimed > 0 || result.EntriesRemoved > 0)
        {
            var removed = new ScanSize(result.BytesReclaimed, result.BytesReclaimed, Entries: result.EntriesRemoved);
            parts.Add($"{FreeSpace.Format(removed)} removed");
        }

        if (result.BytesRequested > 0)
        {
            parts.Add($"{FreeSpace.Format(result.BytesRequested)} asked to release");
        }

        if (result.BytesScheduled > 0)
        {
            parts.Add($"{FreeSpace.Format(result.BytesScheduled)} to be removed when its program next starts");
        }

        return parts.Count == 0 ? "Nothing removed" : string.Join(", ", parts);
    }

    /// <summary>
    /// What the location left, cause by cause.
    ///
    /// <para>A step that failed with nothing counted against it says why in its own words: a command not
    /// run because the program it needs has closed, or an update holding the store. Its counts are left
    /// out of the clauses, because its sentence already accounts for everything it did not remove. A step
    /// that counted what Windows refused is described by the counts instead, because its sentence says
    /// the same thing the clauses do.</para>
    /// </summary>
    private static IReadOnlyList<string> LeftBy(CleanupResult result)
    {
        var told = new List<string>();

        if (result.Interrupted)
        {
            told.Add("The clean was cancelled before this location finished.");
        }

        var counted = result.Steps.Where(step => WhyItFailed(step) is null).ToList();

        told.AddRange(
            LeftInPlace.Each(
                    counted.Aggregate(Refusals.None, (total, step) => total + step.Refused),
                    counted.Aggregate(FolderRefusals.None, (total, step) => total + step.RefusedFolders),
                    counted.Sum(step => step.Kept),
                    counted.Sum(step => step.Spared),
                    counted.Sum(step => step.MailStores),
                    counted.Sum(step => step.UserWork),
                    counted.Sum(step => step.Unlisted),
                    counted.Sum(step => step.Unreleased))
                .Select(clause => clause + "."));

        told.AddRange(result.Steps.Select(WhyItFailed).OfType<string>().Distinct(StringComparer.Ordinal));

        return told;
    }

    /// <summary>The sentence a failed step gives for itself, or null where its counts describe it instead.</summary>
    private static string? WhyItFailed(StepOutcome step) =>
        step is { Succeeded: false, Message: { Length: > 0 } why, Unlisted: 0 }
        && step.Refused.IsEmpty
        && step.RefusedFolders.IsEmpty
            ? why
            : null;
}
