namespace Deguffer.Core.Exploring.Layout;

/// <summary>What the picture does once a mouse dragging it lets go (see <see cref="MapFling.LetGo"/>).</summary>
public enum MapLetGo
{
    /// <summary>The drag stretched it past an edge, and it springs back inside the picture.</summary>
    SpringBack,

    /// <summary>It stays where the hand put it, which is where it comes to rest.</summary>
    Rest,

    /// <summary>It carries on at the speed the hand had, and slows to a stop.</summary>
    Fling,
}
