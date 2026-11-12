using System.Collections.Frozen;

namespace Deguffer.Core.Safety;

/// <summary>
/// Which running processes are one program's, by name: the names it runs under, and the beginnings of
/// names that carry a release number or a platform.
///
/// <para>A fixed list cannot name a program its publisher ships as <c>tool-windows-x64-v1.2.3.exe</c>,
/// because the next release is another name, and a program missed here is a program whose files are
/// deleted while it writes them. A beginning must still be specific enough to name only that program:
/// <c>eim-gui-windows-</c>, never <c>eim</c>, which would hold a row back for any program whose name
/// happens to start the same way.</para>
///
/// <para>Names compare as <see cref="IProcessInspector.FindRunning(IEnumerable{string})"/> compares
/// them: without extension, case-insensitive.</para>
/// </summary>
public sealed class ProcessNameRule
{
    private readonly IReadOnlyList<string> _names;
    private readonly FrozenDictionary<string, int> _order;
    private readonly IReadOnlyList<string> _beginnings;

    private ProcessNameRule(IReadOnlyList<string> names, IReadOnlyList<string> beginnings)
    {
        _names = names;
        _order = names
            .Select((name, index) => (name, index))
            .DistinctBy(entry => entry.name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(entry => entry.name, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        _beginnings = beginnings;
    }

    /// <summary>A rule that matches exactly <paramref name="names"/>.</summary>
    public static ProcessNameRule Named(params IEnumerable<string> names) => new([.. names], []);

    /// <summary>This rule, also matching every process whose name starts with one of <paramref name="beginnings"/>.</summary>
    public ProcessNameRule OrStartingWith(params IEnumerable<string> beginnings) => new(_names, [.. _beginnings, .. beginnings]);

    /// <summary>True where no process can match, so an inspector need not read the machine to answer.</summary>
    internal bool MatchesNothing => _names.Count == 0 && _beginnings.Count == 0;

    /// <summary>
    /// Of <paramref name="running"/>, the process names this rule matches: each exact name as the rule
    /// spells it and in the rule's order, then each name a beginning matches, as the machine reports it,
    /// once however many processes share it.
    /// </summary>
    /// <param name="running">The name of every running process, repeated where several share one.</param>
    public IReadOnlyList<string> Among(IEnumerable<string> running)
    {
        var named = new bool[_names.Count];
        var started = new List<string>();

        foreach (var name in running)
        {
            if (_order.TryGetValue(name, out var index))
            {
                named[index] = true;
            }
            else if (StartsLikeOne(name) && !started.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                started.Add(name);
            }
        }

        return [.. _names.Where((_, index) => named[index]), .. started];
    }

    private bool StartsLikeOne(string name)
    {
        foreach (var beginning in _beginnings)
        {
            if (name.StartsWith(beginning, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
