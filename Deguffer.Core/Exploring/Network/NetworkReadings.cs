using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The network sources' answers for the life of one Path… box session or one network browser:
/// each asked off the UI thread, at most a few at once, given a time limit, and kept.
///
/// <para><b>Kept for the session</b> (G4), so typing <c>\\nas\</c>, deleting a letter and typing it
/// again asks the computer once, and a node collapsed and opened again lists from memory. Refresh
/// drops what it names, and only that.</para>
///
/// <para><b>Each answer is waited for within a limit, and the asking is not stopped.</b> A computer
/// that does not answer holds a share listing for about twenty seconds, and nothing can stop the
/// native call early. So the caller stops waiting at <see cref="Limit"/> and is told
/// <see cref="NetworkFailure.NoAnswer"/>, while the call goes on to its end and its answer is kept: a
/// computer that was only slow then answers the next question at once.</para>
///
/// <para><b>Bounded</b> (G4) by holding a slot for as long as the native call runs, not for as long
/// as anybody waits for it. A slot given back at the time limit would let every slow computer in the
/// tree hold a thread-pool thread at once.</para>
///
/// <para><b>Cancelled two ways.</b> The caller's token stops its wait, as text changes or a node
/// closes, and changes nothing kept. Disposing this cancels what has not started yet, as the box or
/// the dialog closes.</para>
/// </summary>
public sealed class NetworkReadings : IDisposable
{
    /// <summary>
    /// How long one answer is waited for. Long enough for a computer on a busy network to list its
    /// shares, and a third of the time the redirector waits for one that is not there.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(7);

    /// <summary>How many native calls run at once, which bounds the threads a tree of slow computers can hold.</summary>
    private const int MostAtOnce = 4;

    private readonly NetworkAccess _access;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots = new(MostAtOnce, MostAtOnce);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Task<NetworkListing<ListedShare>>> _sharesOf = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Keyed in the case each folder was named in, because a share can be case-sensitive and hold
    /// both <c>Photos</c> and <c>photos</c>. A folder typed in two cases is asked twice, which costs
    /// a listing and never shows one folder's contents under the other's name.
    /// </summary>
    private readonly Dictionary<string, Task<NetworkListing<string>>> _foldersOf = new(StringComparer.Ordinal);
    private Task<NetworkListing<NetworkConnection>>? _connected;
    private Task<NetworkListing<NetworkConnection>>? _remembered;
    private Task<NetworkListing<string>>? _computers;

    /// <param name="time">What the time limit is measured on.</param>
    public NetworkReadings(NetworkAccess access, TimeProvider time)
    {
        _access = access;
        _time = time;
    }

    public Task<NetworkListing<NetworkConnection>> ConnectedAsync(CancellationToken ct) =>
        Within(Kept(ref _connected, _ => _access.Connections.Connected()), ct);

    public Task<NetworkListing<NetworkConnection>> RememberedAsync(CancellationToken ct) =>
        Within(Kept(ref _remembered, _ => _access.Connections.Remembered()), ct);

    public Task<NetworkListing<string>> ComputersAsync(CancellationToken ct) =>
        Within(Kept(ref _computers, _access.Neighbourhood.Computers), ct);

    /// <param name="server">The computer's name, without the leading separators.</param>
    public Task<NetworkListing<ListedShare>> SharesAsync(string server, CancellationToken ct) =>
        Within(Kept(_sharesOf, server, _ => _access.Shares.Shares(server)), ct);

    /// <param name="folder">The share or folder, in display form.</param>
    public Task<NetworkListing<string>> FoldersAsync(string folder, CancellationToken ct) =>
        Within(Kept(_foldersOf, folder.TrimEnd('\\'), life => _access.Folders.Folders(folder, life)), ct);

    /// <summary>
    /// Sign in to <paramref name="remote"/> through the Windows credentials prompt. Not given a time
    /// limit, because the reader is typing a password, and not kept. Once connected, what was kept
    /// about that computer is dropped, because it was the answer of an account that could not list it.
    /// </summary>
    public async Task<NetworkSignIn> SignInAsync(string remote, nint owner, CancellationToken ct)
    {
        var signIn = await Task.Run(() => _access.Connections.SignIn(remote, owner), ct).WaitAsync(ct).ConfigureAwait(false);

        if (signIn.Outcome is SignInOutcome.Connected && NetworkNames.ServerOf(remote) is { } server)
        {
            Forget(NetworkNames.Computer(server));
            ForgetConnections();
        }

        return signIn;
    }

    /// <summary>
    /// Drop what was kept about <paramref name="path"/> and everything inside it, so it is asked
    /// again. Every forgetting here drops only answers that have arrived: a question still being
    /// asked is left to finish, because asking it a second time beside the first would only wait
    /// longer, and its answer is the fresh one the reader asked for.
    /// </summary>
    public void Forget(string path)
    {
        lock (_gate)
        {
            if (NetworkNames.ServerOf(path) is { } server && NetworkNames.ShareOf(path) is null)
            {
                Drop(_sharesOf, server);
            }

            foreach (var folder in _foldersOf.Keys.Where(folder => LongPath.Contains(path.TrimEnd('\\'), folder)).ToList())
            {
                Drop(_foldersOf, folder);
            }
        }
    }

    /// <summary>Drop the connections kept, so the mapped, connected and remembered shares are asked again.</summary>
    public void ForgetConnections()
    {
        lock (_gate)
        {
            Drop(ref _connected);
            Drop(ref _remembered);
        }
    }

    /// <summary>Drop the Network folder's computers kept, so discovery is asked again.</summary>
    public void ForgetComputers()
    {
        lock (_gate)
        {
            Drop(ref _computers);
        }
    }

    /// <summary>Drop everything kept, so the whole tree is asked again.</summary>
    public void ForgetAll()
    {
        lock (_gate)
        {
            foreach (var server in _sharesOf.Keys.ToList())
            {
                Drop(_sharesOf, server);
            }

            foreach (var folder in _foldersOf.Keys.ToList())
            {
                Drop(_foldersOf, folder);
            }

            Drop(ref _connected);
            Drop(ref _remembered);
            Drop(ref _computers);
        }
    }

    /// <summary>
    /// Cancel what has not started. The source is not disposed, so a question that arrives after
    /// this, from a handler the closing raced, is answered as cancelled rather than thrown at.
    /// </summary>
    public void Dispose() => _life.Cancel();

    /// <summary>Drop <paramref name="kept"/> where its answer has arrived. Called under <see cref="_gate"/>.</summary>
    private static void Drop<T>(ref Task<NetworkListing<T>>? kept)
    {
        if (kept is { IsCompleted: true })
        {
            kept = null;
        }
    }

    /// <summary>Drop the answer kept under <paramref name="key"/> where it has arrived. Called under <see cref="_gate"/>.</summary>
    private static void Drop<T>(Dictionary<string, Task<NetworkListing<T>>> kept, string key)
    {
        if (kept.TryGetValue(key, out var reading) && reading.IsCompleted)
        {
            kept.Remove(key);
        }
    }

    private Task<NetworkListing<T>> Kept<T>(ref Task<NetworkListing<T>>? kept, Func<CancellationToken, NetworkListing<T>> read)
    {
        lock (_gate)
        {
            return kept ??= Start(read);
        }
    }

    private Task<NetworkListing<T>> Kept<T>(
        Dictionary<string, Task<NetworkListing<T>>> kept, string key, Func<CancellationToken, NetworkListing<T>> read)
    {
        lock (_gate)
        {
            if (!kept.TryGetValue(key, out var reading))
            {
                kept[key] = reading = Start(read);
            }

            return reading;
        }
    }

    /// <summary>The native call, on the thread pool, once a slot is free. See the class comment for why the slot is held to its end.</summary>
    private Task<NetworkListing<T>> Start<T>(Func<CancellationToken, NetworkListing<T>> read)
    {
        var life = _life.Token;

        return Task.Run(
            async () =>
            {
                await _slots.WaitAsync(life).ConfigureAwait(false);

                try
                {
                    return read(life);
                }
                finally
                {
                    _slots.Release();
                }
            },
            life);
    }

    private async Task<NetworkListing<T>> Within<T>(Task<NetworkListing<T>> reading, CancellationToken ct)
    {
        try
        {
            return await reading.WaitAsync(Limit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The call goes on, and its answer is kept for the next question. See the class comment.
            return NetworkListing<T>.Failed(NetworkFailure.NoAnswer);
        }
    }
}
