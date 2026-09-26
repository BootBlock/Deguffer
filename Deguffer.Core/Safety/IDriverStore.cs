namespace Deguffer.Core.Safety;

/// <summary>
/// One third-party driver package Windows has staged in its driver store, as <c>pnputil</c> lists it.
/// </summary>
/// <param name="PublishedName">The name Windows gave it on import, <c>oem21.inf</c>, which is what removes it.</param>
/// <param name="OriginalName">The INF's own name, <c>iwifi.inf</c>.</param>
/// <param name="Provider">Who the INF says provides it.</param>
/// <param name="ClassGuid">The device setup class it installs into.</param>
/// <param name="ExtensionId">
/// The extension it provides, for an extension INF, or null. Two extension packages with one INF name
/// and different identifiers extend different things, so neither supersedes the other.
/// </param>
/// <param name="Date">The date its <c>DriverVer</c> states.</param>
/// <param name="Version">The version its <c>DriverVer</c> states.</param>
/// <param name="InUse">Whether any device is installed with it.</param>
/// <param name="Folder">
/// Its folder in the driver store, or null where Windows would not say. Windows names the folder with a
/// hash, so it is asked rather than derived.
/// </param>
public sealed record DriverPackage(
    string PublishedName,
    string OriginalName,
    string Provider,
    string ClassGuid,
    string? ExtensionId,
    DateOnly Date,
    Version Version,
    bool InUse,
    string? Folder);

/// <summary>What the driver store held, or why it could not be read.</summary>
/// <param name="Packages">Every third-party package, empty where <paramref name="Failure"/> is set.</param>
/// <param name="Unread">How many listed packages were left out because their entry did not read.</param>
/// <param name="Failure">Why the store could not be listed, written for the user, or null.</param>
public sealed record DriverStoreListing(IReadOnlyList<DriverPackage> Packages, int Unread = 0, string? Failure = null)
{
    public static DriverStoreListing Failed(string why) => new([], Failure: why);
}

/// <summary>
/// Windows' driver store, read through its own tools.
///
/// <para>Behind an interface because both halves reach the machine: <c>pnputil</c> lists the packages,
/// and SetupAPI names each one's folder. A test that reached either would plan against the driver
/// store of whoever ran the suite, and a plan's §5.2 and §5.6 claims have to be proven against a store
/// the test built.</para>
/// </summary>
public interface IDriverStore
{
    /// <summary>
    /// Every third-party package in the store. Listing needs no administrator rights, so a plan can
    /// be made before elevating and only acting needs it.
    /// </summary>
    Task<DriverStoreListing> ListAsync(CancellationToken ct);

    /// <summary>
    /// The folder the package Windows now calls <paramref name="publishedName"/> is in, or null where no
    /// package has that name. Asked afresh, because Windows gives a freed name to the next package it
    /// stages.
    /// </summary>
    string? FolderOf(string publishedName);
}
