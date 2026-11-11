namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The shares this account is connected to, has remembered, and can sign in to, as Windows' network
/// provider layer (WNet) keeps them. Behind a seam so the box and the tree are tested on connections
/// a test describes, and never on the network.
/// </summary>
public interface INetworkConnections
{
    /// <summary>The disk shares this account is connected to now, mapped to a letter or not.</summary>
    NetworkListing<NetworkConnection> Connected();

    /// <summary>The disk connections Windows reconnects at sign-in, whether or not they are connected now.</summary>
    NetworkListing<NetworkConnection> Remembered();

    /// <summary>
    /// Connect to <paramref name="remote"/> through the Windows credentials prompt, owned by
    /// <paramref name="owner"/>, so Deguffer never sees or stores the password. Blocks until the
    /// prompt closes and the connection is made or refused.
    /// </summary>
    /// <param name="remote">A share, or a computer's <c>IPC$</c> share for a computer that refused to list its shares.</param>
    NetworkSignIn SignIn(string remote, nint owner);
}

/// <summary>One connection, by the share it reaches and the letter it is mapped to, where it is.</summary>
/// <param name="Remote">The share as <c>\\server\share</c>.</param>
/// <param name="Local">The letter as <c>Z:</c>, or null for a connection with no letter.</param>
public sealed record NetworkConnection(string Remote, string? Local);

/// <summary>How a sign-in through the Windows credentials prompt ended.</summary>
public enum SignInOutcome
{
    Connected,

    /// <summary>The reader closed the prompt. Nothing is said, because nothing went wrong.</summary>
    Cancelled,

    Failed,
}

/// <summary>A sign-in's outcome, and why it failed where it did.</summary>
public readonly record struct NetworkSignIn(SignInOutcome Outcome, NetworkFailure? Failure)
{
    public static NetworkSignIn Connected { get; } = new(SignInOutcome.Connected, null);

    public static NetworkSignIn Cancelled { get; } = new(SignInOutcome.Cancelled, null);

    public static NetworkSignIn Failed(NetworkFailure failure) => new(SignInOutcome.Failed, failure);
}
