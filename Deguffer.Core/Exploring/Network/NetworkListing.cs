namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// Why a network source listed nothing. Told apart because each asks something different of the
/// reader: a share that wants a password can be signed in to, and a name that is wrong cannot.
/// </summary>
public enum NetworkFailure
{
    /// <summary>The computer or share refused this account, and would take another's credentials.</summary>
    NeedsSignIn,

    /// <summary>No computer, share or folder has that name, or the computer is not on the network.</summary>
    NotFound,

    /// <summary>The computer did not answer within the time a listing is given.</summary>
    NoAnswer,

    /// <summary>Windows refused for another reason, which this account cannot fix from here.</summary>
    Refused,
}

/// <summary>
/// What one network source answered: its entries, or why it has none.
///
/// <para>A failure is never an empty list. A server that refused is not a server with no shares,
/// and the tree and the box say which it was (§5.3, read for listings as for scans).</para>
/// </summary>
/// <typeparam name="T">One entry.</typeparam>
public sealed record NetworkListing<T>(IReadOnlyList<T> Entries, NetworkFailure? Failure)
{
    /// <summary>Nothing listed and nothing wrong.</summary>
    public static NetworkListing<T> Empty { get; } = new([], null);

    public static NetworkListing<T> Listed(IReadOnlyList<T> entries) => new(entries, null);

    public static NetworkListing<T> Failed(NetworkFailure failure) => new([], failure);

    public bool Succeeded => Failure is null;
}
