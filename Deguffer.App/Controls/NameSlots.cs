namespace Deguffer.App.Controls;

/// <summary>
/// Which of the map's name controls writes each name of a drawing, so the controls are made once and
/// a name the map goes on showing goes on being written by the control that wrote it.
///
/// <para>A control is kept once made: a drawing with fewer names leaves the rest over
/// (<see cref="Surplus"/>) for the map to put away, rather than throw away the controls and their
/// brushes, transforms and animations for the next drawing with more to build again. A zoom in and
/// out at 4K moves between about a thousand names and a third of that.</para>
///
/// <para>Within one tree, a name is handed to the control that named its node last time, so its
/// text, its room and its measurement are those it already has, and nothing is written or measured
/// again. A zoom moves a name to another place in the drawing's order, so a control handed out by
/// position would write a different name at almost every drawing. Another tree's nodes are not the
/// same nodes, so its names are handed out in order.</para>
///
/// <para>Separate from <see cref="ExploreLabels"/>, which owns the controls and the composition behind
/// them, because a test host cannot make the window's elements: this is what decides which control is
/// whose, and a test proves it with controls of its own (G1).</para>
/// </summary>
/// <typeparam name="TSlot">One control, as the map keeps it.</typeparam>
/// <param name="make">Makes a control the drawings so far have not needed, and puts it on the map.</param>
internal sealed class NameSlots<TSlot>(Func<TSlot> make)
    where TSlot : class
{
    private readonly List<TSlot> _slots = [];

    /// <summary>Per control, the drawing that last took it, so taking one needs no flags to clear.</summary>
    private readonly List<int> _taken = [];

    /// <summary>Per name of the drawing, the index of the control that writes it.</summary>
    private readonly List<int> _slotOf = [];

    private readonly List<TSlot> _assigned = [];

    private readonly List<NameHistory> _histories = [];

    private readonly List<TSlot> _surplus = [];

    /// <summary>
    /// The nodes the last drawing named, and the control that named each. Swapped with
    /// <see cref="_naming"/> rather than rebuilt (G5).
    /// </summary>
    private Dictionary<int, int> _named = [];

    private Dictionary<int, int> _naming = [];

    private int _drawing;

    /// <summary>Every control made, shown or put away.</summary>
    public IReadOnlyList<TSlot> All => _slots;

    /// <summary>The control that writes each name of the last drawing, in the drawing's order.</summary>
    public IReadOnlyList<TSlot> Assigned => _assigned;

    /// <summary>What each name of the last drawing was before it, in the drawing's order.</summary>
    public IReadOnlyList<NameHistory> Histories => _histories;

    /// <summary>The controls the last drawing left over, for the map to put away until one needs them.</summary>
    public IReadOnlyList<TSlot> Surplus => _surplus;

    /// <summary>
    /// Hand each of <paramref name="names"/> a control, for the node <paramref name="nodeOf"/> says
    /// it names. Where <paramref name="sameTree"/>, the nodes are those of the last drawing's tree.
    /// </summary>
    public void Assign<TName>(IReadOnlyList<TName> names, Func<TName, int> nodeOf, bool sameTree)
    {
        _drawing++;
        _slotOf.Clear();
        _assigned.Clear();
        _histories.Clear();
        _surplus.Clear();
        _naming.Clear();

        if (!sameTree)
        {
            _named.Clear();
        }

        // Every node named last time first, so a name new to this drawing never takes the control of
        // one that is still on the map. The same node named twice keeps its control for the first.
        for (var i = 0; i < names.Count; i++)
        {
            var node = nodeOf(names[i]);

            if (_named.TryGetValue(node, out var slot) && _taken[slot] != _drawing)
            {
                _taken[slot] = _drawing;
                _slotOf.Add(slot);
                _histories.Add(NameHistory.NamedHere);
            }
            else
            {
                _slotOf.Add(-1);
                _histories.Add(_named.ContainsKey(node) ? NameHistory.NamedElsewhere : NameHistory.Unnamed);
            }
        }

        var free = 0;

        for (var i = 0; i < names.Count; i++)
        {
            if (_slotOf[i] < 0)
            {
                while (free < _slots.Count && _taken[free] == _drawing)
                {
                    free++;
                }

                if (free == _slots.Count)
                {
                    _slots.Add(make());
                    _taken.Add(0);
                }

                _taken[free] = _drawing;
                _slotOf[i] = free;
            }

            _assigned.Add(_slots[_slotOf[i]]);
            _naming.TryAdd(nodeOf(names[i]), _slotOf[i]);
        }

        for (var slot = 0; slot < _slots.Count; slot++)
        {
            if (_taken[slot] != _drawing)
            {
                _surplus.Add(_slots[slot]);
            }
        }

        (_named, _naming) = (_naming, _named);
    }

    /// <summary>Forget every control and every name, for a map that has taken its controls off.</summary>
    public void Clear()
    {
        _slots.Clear();
        _taken.Clear();
        _slotOf.Clear();
        _assigned.Clear();
        _histories.Clear();
        _surplus.Clear();
        _named.Clear();
    }
}

/// <summary>What a name of a drawing was in the drawing before it (see <see cref="NameSlots{TSlot}"/>).</summary>
internal enum NameHistory
{
    /// <summary>Its node was not named, or the drawing before was of another tree.</summary>
    Unnamed,

    /// <summary>Its node was named, by the control that writes it now.</summary>
    NamedHere,

    /// <summary>Its node was named, by a control another name of this drawing has, because the drawing names the node twice.</summary>
    NamedElsewhere,
}
