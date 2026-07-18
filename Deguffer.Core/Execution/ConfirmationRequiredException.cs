using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Execution was asked to run a plan the user has not authorised to the standard §7 sets for its
/// tier. Carries the <see cref="Requirement"/> so a caller can present it rather than having to
/// re-derive what was missing.
/// </summary>
public sealed class ConfirmationRequiredException(ConfirmationRequirement requirement)
    : InvalidOperationException(Describe(requirement))
{
    public ConfirmationRequirement Requirement { get; } = requirement;

    private static string Describe(ConfirmationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        return requirement.Level == ConfirmationLevel.Refused
            ? $"'{requirement.ProviderName}' is {requirement.Tier.ToDisplayName()} and is never executable."
            : $"'{requirement.ProviderName}' is {requirement.Tier.ToDisplayName()} and needs " +
              $"{requirement.Level} confirmation before it can be executed.";
    }
}
