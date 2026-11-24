namespace Deguffer.Core.Execution;

/// <summary>
/// The list a finished clean states under its §5.6 verdict: a line for each location the run cleaned,
/// the heading that names the list while it is folded away, and what the next scan does with a refusal.
/// </summary>
/// <param name="Rows">A line for each location, in the order the run cleaned them.</param>
/// <param name="Heading">
/// What the list holds, said before it is opened: how many locations it covers and how many of them
/// left something behind. A folded list headed only "Details" would hide that a row fell short.
/// </param>
/// <param name="Note">
/// What the next scan does with whatever Windows still refuses, or empty where nothing was refused. Said
/// once under the list rather than on every line, because it answers the question every refusal raises:
/// will this row keep offering what it cannot take?
/// </param>
public sealed record RunRows(IReadOnlyList<RunRow> Rows, string Heading, string Note)
{
    public static RunRows For(IReadOnlyList<CleanupResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        RunRow[] rows = [.. results.Select(RunRow.Of)];

        // Qualified, because one refusal is invisible to the preview's question: a running program's own
        // files open for deletion and refuse only the deletion. See DeletionProbe.
        var note = results.Any(r => !r.Refused.IsEmpty)
            ? "The next scan leaves out whatever is still refused, apart from a running program's own files."
            : string.Empty;

        return new RunRows(rows, HeadingFor(rows), note);
    }

    private static string HeadingFor(IReadOnlyList<RunRow> rows)
    {
        var left = rows.Count(row => row.LeftSomething);

        var removed = Counted.Form(rows.Count, "What the location removed", $"What each of the {rows.Count:N0} locations removed");

        return left == 0
            ? removed
            : removed + Counted.Form(rows.Count, ", and what it left behind", $", and what {left:N0} of them left behind");
    }
}
