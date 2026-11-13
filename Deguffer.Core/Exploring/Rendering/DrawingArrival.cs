using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the map will ask of a drawing as it arrives, beyond what every drawing answers: where its
/// shapes travel from the drawing on screen, and what a card's light lights on it.
///
/// <para>Each answer reads every shape of the drawing, about a hundred thousand of them at 4K, but
/// is fixed for the drawing, so it is worked out once on the worker that makes the drawing
/// (<see cref="Prepare"/>) and the UI thread only reads it as the drawing lands.</para>
/// </summary>
/// <param name="From">
/// The drawing on screen that the new drawing's shapes travel from, the same place laid out again,
/// or null where no move is owed (<see cref="ShapeTravel"/>).
/// </param>
/// <param name="Light">The light a card holds on the map, lit again on every new drawing, or null.</param>
public sealed record DrawingArrival(ExploreSurface? From, MapLight? Light)
{
    /// <summary>Work out what the map will ask of <paramref name="drawing"/> as it arrives.</summary>
    public void Prepare(ExploreSurface drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);

        if (From is not null)
        {
            ShapeTravel.Prepare(From, drawing);
        }

        Light?.Prepare(drawing);
    }
}
