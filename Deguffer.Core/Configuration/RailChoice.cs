namespace Deguffer.Core.Configuration;

/// <summary>
/// How the window's navigation rail is drawn: with its labels, folded to its icons, or as the
/// window's width says. See <see cref="Viewing.RailFold"/> for the width and for how a toggle
/// becomes one of these.
///
/// <para>Three values rather than a flag, because the rail folds on its own on a narrow window, and
/// a flag cannot tell "the reader wants the labels" from "nobody has said". Stored by name.</para>
/// </summary>
public enum RailChoice
{
    /// <summary>Labels on a wide window, icons on a narrow one. Nobody has said otherwise.</summary>
    Automatic = 0,

    /// <summary>Labels at every width, because the reader opened the rail where it would fold.</summary>
    Open = 1,

    /// <summary>Icons at every width, because the reader folded the rail where it would open.</summary>
    Collapsed = 2,
}
