namespace Deguffer.Core.Exploring.Layout;

/// <summary>What follows the tracker carrying out a move of the camera's origin (see <see cref="MapOriginMove.Carried"/>).</summary>
public enum MapOriginCarried
{
    /// <summary>The map was following a request of its own, whose reports say what follows.</summary>
    Unchanged,

    /// <summary>The move stopped a coast where it went, and the camera goes on moving until the tracker rests there.</summary>
    Coasts,

    /// <summary>The move stopped a coast that had already rested, and the camera is at rest where it went.</summary>
    Rests,
}
