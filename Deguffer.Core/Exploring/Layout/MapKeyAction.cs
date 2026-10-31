namespace Deguffer.Core.Exploring.Layout;

/// <summary>What a key does on the map, as <see cref="MapKeys.Resolve"/> decides it.</summary>
public abstract record MapKeyAction
{
    private MapKeyAction()
    {
    }

    /// <summary>Move the keyboard's focus to the shape next to the focused one in <paramref name="Direction"/>.</summary>
    public sealed record Focus(MapDirection Direction) : MapKeyAction;

    /// <summary>Move the camera as <paramref name="Key"/> asks, where the picture can move.</summary>
    public sealed record Camera(MapKey Key) : MapKeyAction;

    /// <summary>Open the shape the keyboard is on.</summary>
    public sealed record Open : MapKeyAction;

    /// <summary>Go up a folder.</summary>
    public sealed record Ascend : MapKeyAction;
}
