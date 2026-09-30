namespace Deguffer.Core.InstalledApps;

/// <summary>One entry, with what Windows shows of it and what the evidence says about it (§7.3).</summary>
/// <param name="Name">The display name, or the key's own name where the entry has none.</param>
/// <param name="Values">The values as read, kept so a later action can tell whether the key changed.</param>
/// <param name="NoRemove">Whether the entry sets <c>NoRemove</c>, which removes Windows' own uninstall button.</param>
public sealed record InstalledEntry(
    UninstallKey Key,
    string Name,
    string? Publisher,
    string? Version,
    UninstallValues Values,
    EntryVisibility Visibility,
    UninstallCommand Command,
    StandingVerdict Standing,
    bool NoRemove)
{
    /// <summary>Whether Windows lists the entry. A hidden one is shown only on request.</summary>
    public bool IsListed => Visibility is EntryVisibility.Listed;

    /// <summary>Whether it belongs in the stale list rather than the installed one.</summary>
    public bool IsStale => Standing.IsStale;
}
