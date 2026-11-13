using Deguffer.Core.Viewing;

namespace Deguffer.App.Shell;

/// <summary>
/// Where <see cref="RowGhosts"/> draws the ghosts of a change's leaving rows, in the three steps it
/// takes them in: every ghost is added, the layer is laid out once, and only then is each one faded.
///
/// <para>Laying the layer out lays out the whole window, not only the ghosts, so a change's ghosts
/// share one pass. The order is <see cref="RowGhosts"/>' to keep, and this seam lets a test count
/// the passes without a window.</para>
/// </summary>
/// <typeparam name="TGhost">What the layer draws one ghost as.</typeparam>
internal interface IGhostLayer<TGhost>
{
    /// <summary>Put a ghost of <paramref name="item"/> where its row stood, not yet laid out.</summary>
    TGhost Add(object item, RowGhosts.Stood stood);

    /// <summary>Lay out every ghost added since the last pass, so each can be read where it stands.</summary>
    void LayOut();

    /// <summary>
    /// Make <paramref name="ghost"/>, now laid out, unreachable, align it to where its row's content
    /// stood, and fade it out as <paramref name="played"/> plays.
    /// </summary>
    void Fade(TGhost ghost, RowGhosts.Stood stood, Motion played);
}
