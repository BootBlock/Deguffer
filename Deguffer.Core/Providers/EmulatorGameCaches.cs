namespace Deguffer.Core.Providers;

/// <summary>
/// Cache folders an emulator keeps once for each game, inside a folder of its own for each game, and
/// the rule for which folders are a game's.
///
/// <para>The folder of games is the one folder listed to find them. Nothing in it is a target, and
/// only a folder the rule names as a game's is entered, so a folder the user made beside them is
/// never looked inside.</para>
/// </summary>
/// <param name="GamesFolder">
/// The folder holding a folder for each game, built from a proven root. Never a target itself, and
/// asserted to survive.
/// </param>
/// <param name="IsGame">Whether a folder in it is one game's, by its name.</param>
/// <param name="CacheFoldersIn">The cache folders inside one game's folder.</param>
/// <param name="ProtectedNames">
/// Entries in each game's folder with a reason of their own to survive, such as its settings.
/// </param>
public sealed record EmulatorGameCaches(
    string GamesFolder,
    Predicate<string> IsGame,
    Func<string, IReadOnlyList<EmulatorCacheFolder>> CacheFoldersIn,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames);
