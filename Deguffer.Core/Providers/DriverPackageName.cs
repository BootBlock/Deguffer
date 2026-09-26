using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether the <c>oem&lt;N&gt;.inf</c> name a <c>pnputil /delete-driver</c> step was planned with still
/// names the package folder it was planned to remove. See <see cref="ICommandTargetCheck"/> for why a
/// name that has changed hands must stop the step.
/// </summary>
/// <param name="Store">Asked afresh at the clean, never from the listing the plan was made from.</param>
/// <param name="PublishedName">The name the step hands to <c>pnputil</c>.</param>
public sealed record DriverPackageName(IDriverStore Store, string PublishedName) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        return Store.FolderOf(PublishedName) switch
        {
            null => $"Windows no longer has a driver package called {PublishedName}",
            var now when !Same(now, step.Removes) =>
                $"Windows now gives the name {PublishedName} to a different driver package, at {LongPath.Display(now)}",
            _ => null,
        };
    }

    private static bool Same(string now, string? planned) =>
        planned is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(LongPath.Display(now)),
            Path.TrimEndingDirectorySeparator(LongPath.Display(planned)),
            StringComparison.OrdinalIgnoreCase);
}
