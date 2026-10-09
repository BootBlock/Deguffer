namespace Deguffer.Core.Duplicates;

/// <summary>What a location is for in a search (§7.4).</summary>
public enum LocationRole
{
    /// <summary>Searched and matched, and its copies may be marked.</summary>
    Search,

    /// <summary>
    /// Searched and matched, and never marked or removed under any rule or preference: "find what in
    /// Downloads is already in my Photos". Where a folder is given in both roles, this one wins.
    /// </summary>
    Reference,
}

/// <summary>A drive or folder the user chose to search, and its role.</summary>
/// <param name="Path">A full path, as the user gave it.</param>
public sealed record SearchLocation(string Path, LocationRole Role = LocationRole.Search);
