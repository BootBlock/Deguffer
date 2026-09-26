using Deguffer.Core.Execution;

namespace Deguffer.Core.Choosing;

/// <summary>
/// The columns a plan's items are listed under, and each item's value in each of them.
///
/// <para>Worked out across the whole plan rather than per item, because a list whose columns moved
/// from one row to the next could not be read down. A label becomes a column the first time any item
/// names it, in the order the items name them. An item that says nothing under a label shows nothing
/// there, rather than a value that belongs to a different column.</para>
///
/// <para>Worked out once per plan and handed to every row, because a plan can hold a thousand items
/// and each row asking for itself would walk all the others (G4).</para>
/// </summary>
public sealed class ItemColumns
{
    private static readonly ItemColumns None = new([]);

    private readonly Dictionary<string, int> _positions;

    private ItemColumns(IReadOnlyList<string> labels)
    {
        Labels = labels;
        _positions = new Dictionary<string, int>(labels.Count, StringComparer.Ordinal);

        for (var i = 0; i < labels.Count; i++)
        {
            _positions[labels[i]] = i;
        }
    }

    /// <summary>The column headings, in order. Empty where no item carries a facet.</summary>
    public IReadOnlyList<string> Labels { get; }

    public static ItemColumns Of(IEnumerable<CleanupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        List<string> labels = [];

        // Ordinal, because a label is text a provider writes once as a constant, never a folder name.
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            foreach (var facet in step.Facets)
            {
                if (seen.Add(facet.Label))
                {
                    labels.Add(facet.Label);
                }
            }
        }

        return labels.Count == 0 ? None : new ItemColumns(labels);
    }

    /// <summary>
    /// <paramref name="step"/>'s value under each of <see cref="Labels"/>, in the same order, with an
    /// empty string where it has none. Where a step names one label twice, the first value stands.
    /// </summary>
    public IReadOnlyList<string> ValuesOf(CleanupStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (Labels.Count == 0)
        {
            return [];
        }

        var values = new string?[Labels.Count];

        foreach (var facet in step.Facets)
        {
            if (_positions.TryGetValue(facet.Label, out var position))
            {
                values[position] ??= facet.Value;
            }
        }

        return [.. values.Select(value => value ?? string.Empty)];
    }
}
