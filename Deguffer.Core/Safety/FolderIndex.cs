namespace Deguffer.Core.Safety;

/// <summary>
/// Items filed under folders, so that what holds a path is found by looking up each folder above the
/// path rather than by asking every item (#706).
///
/// <para><b>Every folder that <see cref="LongPath.Contains(string, string)"/> says holds a path is
/// one the walk looks up.</b> It compares by <see cref="StringComparison.OrdinalIgnoreCase"/>, as
/// the index does, and a folder holds a path only at a backslash of the path, which is where the walk
/// stops. A key has its trailing separators dropped, so <c>C:\</c> is found at the first backslash of
/// <c>C:\a</c> as <c>C:\a</c> is at the second.</para>
/// </summary>
/// <typeparam name="T">What is filed.</typeparam>
internal sealed class FolderIndex<T>
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly Dictionary<string, T[]>.AlternateLookup<ReadOnlySpan<char>> _filed;

    /// <param name="items">What to file. Each folder keeps the items filed under it in this order.</param>
    /// <param name="foldersOf">Every folder an item is filed under, in any form. An item named twice under one folder is filed once.</param>
    public FolderIndex(IEnumerable<T> items, Func<T, IEnumerable<string>> foldersOf)
    {
        Dictionary<string, List<T>> filing = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> filedHere = new(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            filedHere.Clear();

            foreach (var folder in foldersOf(item))
            {
                var key = folder.TrimEnd(Separators);

                if (!filedHere.Add(key))
                {
                    continue;
                }

                if (!filing.TryGetValue(key, out var filed))
                {
                    filing[key] = filed = [];
                }

                filed.Add(item);
            }
        }

        _filed = filing
            .ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// What is filed at each folder from <paramref name="place"/> upward, the deepest first, passing
    /// over the folders nothing is filed at. An item filed at two of the folders comes up at each.
    /// </summary>
    public Holders Holding(string place) => new(_filed, place.AsSpan().TrimEnd(Separators));

    /// <summary>What is filed at exactly <paramref name="place"/>, and nothing where there is none.</summary>
    public ReadOnlySpan<T> At(string place) =>
        _filed.TryGetValue(place.AsSpan().TrimEnd(Separators), out var filed) ? filed : [];

    /// <summary>The walk <see cref="Holding"/> makes, in the shape <c>foreach</c> reads without allocating.</summary>
    internal ref struct Holders
    {
        private readonly Dictionary<string, T[]>.AlternateLookup<ReadOnlySpan<char>> _filed;
        private ReadOnlySpan<char> _next;
        private bool _more;

        public Holders(Dictionary<string, T[]>.AlternateLookup<ReadOnlySpan<char>> filed, ReadOnlySpan<char> place)
        {
            _filed = filed;
            _next = place;
            _more = true;
        }

        /// <summary>What is filed at the folder the walk has reached, in the order it was filed.</summary>
        public T[] Current { get; private set; } = [];

        public readonly Holders GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_more)
            {
                var folder = _next;
                var separator = folder.LastIndexOf(Path.DirectorySeparatorChar);

                _more = separator >= 0;
                _next = _more ? folder[..separator] : default;

                if (_filed.TryGetValue(folder, out var filed))
                {
                    Current = filed;
                    return true;
                }
            }

            return false;
        }
    }
}
