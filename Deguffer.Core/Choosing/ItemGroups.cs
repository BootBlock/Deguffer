using Deguffer.Core.Execution;

namespace Deguffer.Core.Choosing;

/// <summary>How a plan's items are gathered under headings and put in order for the reader.</summary>
public static class ItemGroups
{
    /// <summary>
    /// <paramref name="items"/> gathered under their headings, the largest group first and the largest
    /// item first within each (§7: sort by size). Ties keep the provider's order, so a list of equal
    /// items reads the same from one preview to the next.
    ///
    /// <para>Headings are compared without regard to case, because most of them are folder names and
    /// NTFS does not tell those apart. The first spelling met is the one shown. Items with no heading
    /// form one group of their own, named null, which is sized and placed like any other.</para>
    ///
    /// <para>Generic over what is listed, because the shell lists its own view of each step and still
    /// needs this order. <paramref name="stepOf"/> is how each is sized and grouped.</para>
    /// </summary>
    public static IReadOnlyList<ItemGroup<T>> Of<T>(IEnumerable<T> items, Func<T, CleanupStep> stepOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(stepOf);

        List<(string? Name, List<T> Items)> gathered = [];
        Dictionary<string, int> named = new(StringComparer.OrdinalIgnoreCase);
        int? unnamed = null;

        foreach (var item in items)
        {
            var name = (stepOf(item) as DeleteStep)?.Group;

            int position;
            if (name is null)
            {
                position = unnamed ??= Add(gathered, null);
            }
            else if (!named.TryGetValue(name, out position))
            {
                position = named[name] = Add(gathered, name);
            }

            gathered[position].Items.Add(item);
        }

        // OrderByDescending is a stable sort, which is what keeps ties in the provider's order.
        return
        [
            .. gathered
                .Select(group => new ItemGroup<T>(
                    group.Name,
                    [.. group.Items.OrderByDescending(item => stepOf(item).EstimatedBytes)]))
                .OrderByDescending(group => group.Items.Sum(item => stepOf(item).EstimatedBytes)),
        ];
    }

    private static int Add<T>(List<(string? Name, List<T> Items)> gathered, string? name)
    {
        gathered.Add((name, []));
        return gathered.Count - 1;
    }
}
