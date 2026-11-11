namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// Which of a computer's shares are offered: shared folders a person put there, and nothing else.
///
/// <para>An administrative share (<c>C$</c>, <c>ADMIN$</c>) is the whole of a disk or of Windows,
/// opened to administrators for management, and is not a place anybody means by "the share on the
/// NAS". <c>IPC$</c>, printers and devices hold no folders. A name ending in <c>$</c> is one its owner
/// hid from listings on purpose, so it is left out too: a person who knows it types it.</para>
/// </summary>
public static class ShareFilter
{
    /// <summary>The shares of <paramref name="computer"/> worth offering, by name, A to Z.</summary>
    public static IReadOnlyList<NetworkPlace> Offered(string computer, IReadOnlyList<ListedShare> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        var path = NetworkNames.Computer(computer);

        return
        [
            .. listed
                .Where(IsOffered)
                .OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase)
                .Select(share => new NetworkPlace(NetworkNames.Inside(path, share.Name), share.Name, NetworkPlaceKind.Share)),
        ];
    }

    private static bool IsOffered(ListedShare share) =>
        share is { Kind: ShareKind.Disk, IsSpecial: false }
        && share.Name.Length > 0
        && !share.Name.EndsWith('$');
}
