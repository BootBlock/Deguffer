namespace Deguffer.Core.Choosing;

/// <summary>A heading in a list of items, and the items listed under it.</summary>
/// <param name="Name">
/// The heading, or null for the items their provider put under no heading. See
/// <see cref="Execution.CleanupStep.Group"/>.
/// </param>
/// <param name="Items">What is listed under it, in the order <see cref="ItemGroups.Of"/> gives.</param>
public sealed record ItemGroup<T>(string? Name, IReadOnlyList<T> Items);
