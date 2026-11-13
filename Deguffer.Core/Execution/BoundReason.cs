namespace Deguffer.Core.Execution;

/// <summary>
/// Why a <see cref="RemovalBounds"/> holds an entry back, which decides how the removal reports it.
/// </summary>
internal enum BoundReason : byte
{
    /// <summary>Not held back by its path.</summary>
    None,

    /// <summary>It holds somebody's work. See <see cref="RemovalBounds.UserWork"/>.</summary>
    UserWork,

    /// <summary>Something is using it. See <see cref="RemovalBounds.Spared"/>.</summary>
    Spared,

    /// <summary>Another row offers it. See <see cref="RemovalBounds.OwnedElsewhere"/>.</summary>
    OwnedElsewhere,
}
