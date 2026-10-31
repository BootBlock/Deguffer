using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// What a drawing of the picture on screen is laid out and painted from: the folder drawn of the tree
/// the page handed over, in the colours and the look every drawing of that picture shares, on the
/// ground under it. Taken on the UI thread, because the layout runs on a worker. See
/// <see cref="ExploreCanvas"/>.
/// </summary>
/// <param name="Tree">The tree the page handed over last.</param>
/// <param name="Drawn">
/// The folder drawn, which a change of folder may be passing through. See <see cref="FolderFlight.Drawn"/>.
/// </param>
/// <param name="Colours">What the colours say in every drawing of the picture on screen.</param>
/// <param name="Look">How much room a treemap leaves round what each folder holds, and how every shape is finished.</param>
/// <param name="Ground">The canvas ground, for the theme the map is in. See <see cref="MapGround"/>.</param>
internal sealed record ExplorePainting(ISizedTree Tree, MapPicture Drawn, ShapeColours Colours, MapLook Look, TileColour Ground);
