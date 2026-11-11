namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The shares one named computer offers (<c>NetShareEnum</c>). It asks the computer directly, so it
/// answers with network discovery off, and for a computer discovery cannot find. Behind a seam so the
/// shares a test describes decide what is offered, and the network is never asked.
/// </summary>
public interface IShareLister
{
    /// <summary>
    /// Every share <paramref name="server"/> offers, hidden and printer shares included, because what
    /// is left out is Core's decision (<see cref="ShareFilter"/>). Blocks until the computer answers,
    /// which for one that does not can be about twenty seconds.
    /// </summary>
    /// <param name="server">The computer's name or address, without the leading separators.</param>
    NetworkListing<ListedShare> Shares(string server);
}

/// <summary>What a share is, by the type <c>NetShareEnum</c> gives it.</summary>
public enum ShareKind
{
    /// <summary>A shared folder, the one kind Explore can scan.</summary>
    Disk,

    PrintQueue,

    Device,

    /// <summary>The interprocess share, <c>IPC$</c>.</summary>
    Ipc,
}

/// <summary>One share as the computer listed it.</summary>
/// <param name="Name">The share's name, as the computer sent it.</param>
/// <param name="Kind">See <see cref="ShareKind"/>.</param>
/// <param name="IsSpecial">Whether Windows marks it an administrative share (<c>STYPE_SPECIAL</c>), such as <c>C$</c> or <c>ADMIN$</c>.</param>
public sealed record ListedShare(string Name, ShareKind Kind, bool IsSpecial);
