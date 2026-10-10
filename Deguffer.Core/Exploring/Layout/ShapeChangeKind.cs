namespace Deguffer.Core.Exploring.Layout;

/// <summary>What became of one shape between two layouts of the same place. See <see cref="ShapeChanges"/>.</summary>
public enum ShapeChangeKind
{
    /// <summary>Drawn in both, somewhere else or at another size.</summary>
    Moved,

    /// <summary>Drawn only in the later layout: it grows out of the middle of where it is drawn.</summary>
    Appeared,

    /// <summary>Drawn only in the earlier layout: it shrinks into the middle of where it was drawn.</summary>
    Disappeared,
}
