namespace Deguffer.Core.Exploring;

/// <summary>
/// Some of one folder's children in an <see cref="ExploreTree"/>, found by name without regard to
/// case, as Windows names are.
///
/// <para>Keyed by node, with each name read where the tree holds it, so indexing a folder of a
/// hundred thousand files makes no string for any of them, and asking for a name makes none for
/// it.</para>
///
/// <para>Of two names that differ only in case, which a directory with case sensitivity turned on
/// can hold, the first added is the one found.</para>
/// </summary>
internal sealed class ChildrenByName
{
    private readonly HashSet<int> _children;
    private readonly HashSet<int>.AlternateLookup<ReadOnlySpan<char>> _byName;

    public ChildrenByName(ExploreTree tree)
    {
        _children = new HashSet<int>(new ByName(tree));
        _byName = _children.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Add <paramref name="child"/>, unless a child of the same name is already here.</summary>
    public void Add(int child) => _children.Add(child);

    /// <summary>The child called <paramref name="name"/>, or null.</summary>
    public int? Find(ReadOnlySpan<char> name) => _byName.TryGetValue(name, out var child) ? child : null;

    /// <summary>Compares nodes of one tree by their names, ordinally and without regard to case.</summary>
    private sealed class ByName(ExploreTree tree) : IEqualityComparer<int>, IAlternateEqualityComparer<ReadOnlySpan<char>, int>
    {
        public bool Equals(int x, int y) => tree.NameSpanOf(x).Equals(tree.NameSpanOf(y), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(int obj) => GetHashCode(tree.NameSpanOf(obj));

        public bool Equals(ReadOnlySpan<char> alternate, int other) =>
            alternate.Equals(tree.NameSpanOf(other), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOnlySpan<char> alternate) => string.GetHashCode(alternate, StringComparison.OrdinalIgnoreCase);

        // A child is added by its node, never by a name, so there is nothing a name could make.
        public int Create(ReadOnlySpan<char> alternate) =>
            throw new NotSupportedException("A child is added by its node.");
    }
}
