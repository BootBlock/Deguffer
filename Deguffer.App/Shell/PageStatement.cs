namespace Deguffer.App.Shell;

/// <summary>What About says about one page: the page's name, and the sentences said of it.</summary>
/// <param name="Name">The page's name, from <see cref="DestinationNames"/>.</param>
/// <param name="Sentences">Whole sentences, each ending in a full stop.</param>
public sealed record PageStatement(string Name, IReadOnlyList<string> Sentences);
