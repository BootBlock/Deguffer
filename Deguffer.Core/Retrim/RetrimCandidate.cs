using Deguffer.Core.Safety;

namespace Deguffer.Core.Retrim;

/// <summary>One volume a finished clean is offered a retrim on.</summary>
/// <param name="Volume">The volume as it stood after the clean.</param>
/// <param name="Freed">How much its free space grew across the clean, in bytes.</param>
/// <param name="Basis">Why a retrim is offered on it.</param>
public sealed record RetrimCandidate(LocalVolume Volume, long Freed, RetrimBasis Basis);
