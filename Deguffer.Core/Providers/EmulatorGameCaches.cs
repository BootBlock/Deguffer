namespace Deguffer.Core.Providers;

/// <summary>
/// Cache folders an emulator keeps once for each game, inside a folder of its own for each game.
///
/// <para>The folder of games is the one folder listed to find them. Nothing in it is a target, and
/// only a folder named by a Switch title id is entered, so a folder the user made beside them is
/// never looked inside. Every emulator that keeps a folder for each game is a Switch emulator, and
/// each names that folder the same way, see <see cref="SwitchTitleId"/>.</para>
/// </summary>
/// <param name="GamesFolder">
/// The folder holding a folder for each game, built from a proven root. Never a target itself, and
/// asserted to survive.
/// </param>
/// <param name="CacheFoldersIn">The cache folders inside one game's folder.</param>
/// <param name="ProtectedNames">
/// Entries in each game's folder with a reason of their own to survive, such as its settings.
/// </param>
public sealed record EmulatorGameCaches(
    string GamesFolder,
    Func<string, IReadOnlyList<EmulatorCacheFolder>> CacheFoldersIn,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames);
