namespace Deguffer.Core.Safety;

/// <summary>
/// Which reading of the process table an <see cref="ILiveTreeInspector"/> answers from, so a caller
/// can keep what it builds from one reading for as long as the inspector keeps the reading, and no
/// longer.
///
/// <para>Two values one inspector gave are equal only when they name the same reading. A reading
/// dropped, by <see cref="ILiveTreeInspector.Invalidate"/> or by
/// <see cref="ILiveTreeInspector.DiscardOlderThan"/>, is never named again. Values from two
/// inspectors are not comparable, so whatever is kept against one belongs to the inspector it was
/// asked of.</para>
/// </summary>
/// <param name="Number">The reading's place in the order the inspector took them.</param>
public readonly record struct LiveTreeReading(long Number);
