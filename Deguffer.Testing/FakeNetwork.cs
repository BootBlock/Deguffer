using System.Collections.Concurrent;
using Deguffer.Core.Exploring.Network;

namespace Deguffer.Testing;

/// <summary>
/// A network a test describes: the connections this account has, the computers discovery finds,
/// each computer's shares and each folder's folders. An answer can be held back, to stand for a
/// computer that is slow or does not answer, and every question is counted, so a test can say how
/// often the network was asked.
///
/// <para>One fake for the four seams, because a test describes one network, and the four are only
/// the ways of asking it. A computer or folder the test has not described is not there.</para>
/// </summary>
public sealed class FakeNetwork : INetworkConnections, INetworkNeighbourhood, IShareLister, IFolderLister
{
    /// <summary>The key <see cref="Hold"/> and <see cref="Asked"/> use for the Network folder.</summary>
    public const string Discovery = "(discovery)";

    private readonly ConcurrentDictionary<string, NetworkListing<ListedShare>> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetworkListing<string>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The four seams over this one network.</summary>
    public NetworkAccess Access => new(this, this, this, this);

    public NetworkListing<NetworkConnection> ConnectedAnswer { get; set; } = NetworkListing<NetworkConnection>.Empty;

    public NetworkListing<NetworkConnection> RememberedAnswer { get; set; } = NetworkListing<NetworkConnection>.Empty;

    public NetworkListing<string> ComputersAnswer { get; set; } = NetworkListing<string>.Empty;

    /// <summary>What a sign-in answers: connected, unless a test says otherwise.</summary>
    public NetworkSignIn SignInAnswer { get; set; } = NetworkSignIn.Connected;

    /// <summary>Every sign-in asked for, with the window that owned the prompt.</summary>
    public ConcurrentQueue<(string Remote, nint Owner)> SignIns { get; } = new();

    /// <summary>A computer and the shares it lists.</summary>
    public FakeNetwork Computer(string server, params ListedShare[] shares)
    {
        _shares[server] = NetworkListing<ListedShare>.Listed(shares);
        return this;
    }

    /// <summary>A computer whose share listing fails with <paramref name="failure"/>.</summary>
    public FakeNetwork Refusing(string server, NetworkFailure failure)
    {
        _shares[server] = NetworkListing<ListedShare>.Failed(failure);
        return this;
    }

    /// <summary>A share or folder and the folders inside it.</summary>
    public FakeNetwork Folder(string path, params string[] folders)
    {
        _folders[path] = NetworkListing<string>.Listed(folders);
        return this;
    }

    /// <summary>A share or folder whose listing fails with <paramref name="failure"/>.</summary>
    public FakeNetwork FolderFails(string path, NetworkFailure failure)
    {
        _folders[path] = NetworkListing<string>.Failed(failure);
        return this;
    }

    /// <summary>
    /// Hold back every answer for <paramref name="key"/> (a computer, a folder, or
    /// <see cref="Discovery"/>) until <see cref="Release"/>, as a computer that does not answer does.
    /// </summary>
    public void Hold(string key) => _held[key] = new ManualResetEventSlim();

    public void Release(string key)
    {
        if (_held.TryRemove(key, out var gate))
        {
            gate.Set();
        }
    }

    /// <summary>How many times <paramref name="key"/> was asked about.</summary>
    public int Asked(string key) => _asked.GetValueOrDefault(key);

    public NetworkListing<NetworkConnection> Connected() => ConnectedAnswer;

    public NetworkListing<NetworkConnection> Remembered() => RememberedAnswer;

    public NetworkSignIn SignIn(string remote, nint owner)
    {
        SignIns.Enqueue((remote, owner));
        return SignInAnswer;
    }

    public NetworkListing<string> Computers(CancellationToken ct)
    {
        Wait(Discovery, ct);
        return ComputersAnswer;
    }

    public NetworkListing<ListedShare> Shares(string server)
    {
        Wait(server, CancellationToken.None);
        return _shares.GetValueOrDefault(server) ?? NetworkListing<ListedShare>.Failed(NetworkFailure.NotFound);
    }

    public NetworkListing<string> Folders(string folder, CancellationToken ct)
    {
        var key = folder.TrimEnd('\\');

        Wait(key, ct);
        return _folders.GetValueOrDefault(key) ?? NetworkListing<string>.Failed(NetworkFailure.NotFound);
    }

    private void Wait(string key, CancellationToken ct)
    {
        _asked.AddOrUpdate(key, 1, (_, count) => count + 1);

        if (_held.TryGetValue(key, out var gate))
        {
            gate.Wait(ct);
        }
    }
}
