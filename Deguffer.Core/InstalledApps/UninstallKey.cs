namespace Deguffer.Core.InstalledApps;

/// <summary>
/// One entry's key: the <c>Uninstall</c> key it sits under and its own name there.
///
/// <para>The name is compared without case, as the registry compares it, so an entry read twice
/// under names that differ only in case is the same entry.</para>
/// </summary>
public sealed record UninstallKey(UninstallScope Scope, string Name)
{
    /// <summary>The key's full path as it is stored. See <see cref="UninstallScopes.PhysicalPath"/>.</summary>
    public string PhysicalPath => $@"{Scope.PhysicalPath()}\{Name}";

    public bool Equals(UninstallKey? other) =>
        other is not null && Scope == other.Scope && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Scope, StringComparer.OrdinalIgnoreCase.GetHashCode(Name));
}
