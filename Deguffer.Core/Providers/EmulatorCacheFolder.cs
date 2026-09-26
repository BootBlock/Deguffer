using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One folder a cache is taken from, and the rule for what in it is cache.
/// </summary>
/// <param name="Path">The folder. Never a target itself, and asserted to survive.</param>
/// <param name="Kind">
/// Whether the cache in it is directories or files. An entry of the other kind is not cache,
/// whatever its name.
/// </param>
/// <param name="Classify">
/// What an entry of that kind is, by name. Anything the rule does not recognise is Tier 4.
/// </param>
/// <param name="ProtectedNames">
/// Entries in the folder with a reason of their own to survive. Anything unrecognised survives
/// anyway, and is named in the plan as it is found.
/// </param>
public sealed record EmulatorCacheFolder(
    string Path,
    TargetKind Kind,
    Func<string, ChildClassification> Classify,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames);
