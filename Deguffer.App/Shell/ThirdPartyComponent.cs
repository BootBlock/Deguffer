namespace Deguffer.App.Shell;

/// <summary>A component Deguffer ships inside its own folder, and the licence it ships under.</summary>
/// <param name="Name">The component's name, as its publisher gives it.</param>
/// <param name="Licence">The licence's name.</param>
/// <param name="LicenceUri">Where a reader can open its terms.</param>
public sealed record ThirdPartyComponent(string Name, string Licence, Uri LicenceUri)
{
    /// <summary>
    /// The licence link's accessible name: the licence's name, which is all the link shows, then
    /// whose licence it is, which a screen reader on the link alone would not otherwise hear.
    /// </summary>
    public string LicenceLinkName => $"{Licence}, the licence of {Name}";
}
