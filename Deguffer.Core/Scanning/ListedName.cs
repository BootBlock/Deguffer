namespace Deguffer.Core.Scanning;

/// <summary>
/// An entry's name as a listing kept it: a string of its own, characters lent from the listing
/// worker's memory (<see cref="FileNames.AllLent"/>), or nothing, for a file a listing kept without
/// its name.
///
/// <para><b>A lent name can be read only until the worker lists its next directory</b>, which is as
/// long as a walk lets a caller hold a directory's contents. Reading it after that throws, rather
/// than handing back whatever name the memory holds by then: in code that deletes paths, a name read
/// from the wrong listing names the wrong file.</para>
///
/// <para>One reference and two numbers, eight bytes more than the string it stands in for, because
/// every entry any walk lists carries one, and a removal keeps every file's entry until it is
/// done.</para>
/// </summary>
internal readonly struct ListedName : IEquatable<ListedName>
{
    // The string the name is, or the lending it was lent through.
    private readonly object? _source;
    private readonly int _start;
    private readonly int _length;

    private ListedName(object source, int start, int length)
    {
        _source = source;
        _start = start;
        _length = length;
    }

    /// <summary>The name <paramref name="name"/>, held as the string it is.</summary>
    public static ListedName Own(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new ListedName(name, 0, name.Length);
    }

    /// <summary>The <paramref name="length"/> characters lent through <paramref name="lending"/> at <paramref name="start"/>.</summary>
    public static ListedName Lent(ListingNames.Lending lending, int start, int length) =>
        new(lending, start, length);

    /// <summary>Whether there is a name: false only for a file a listing kept without one.</summary>
    public bool Exists => _source is not null;

    /// <summary>The name's characters. Throws where there is no name, or where a lent one was recalled.</summary>
    public ReadOnlySpan<char> Span => _source switch
    {
        string own => own,
        ListingNames.Lending lending => lending.Read(_start, _length),
        _ => throw new InvalidOperationException("The entry was listed without its name."),
    };

    /// <summary>The name as a string: the one it holds, or a new one for a name that was lent.</summary>
    public override string ToString() => _source as string ?? new string(Span);

    public bool Equals(ListedName other) =>
        Exists == other.Exists && (!Exists || Span.SequenceEqual(other.Span));

    public override bool Equals(object? obj) => obj is ListedName other && Equals(other);

    public override int GetHashCode() => Exists ? string.GetHashCode(Span) : 0;

    public static bool operator ==(ListedName left, ListedName right) => left.Equals(right);

    public static bool operator !=(ListedName left, ListedName right) => !left.Equals(right);
}
