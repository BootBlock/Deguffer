using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// Answers the Path… box and the network browser for one session: reads what each question needs
/// through <see cref="NetworkReadings"/>, and hands the answers to the rules that decide what is
/// shown (<see cref="NetworkSuggestions"/>, <see cref="NetworkTree"/>). It decides nothing itself.
///
/// <para>The sources an empty box or a root reads are asked together, and what has answered is
/// shown at once: a Network folder that waits on discovery neither holds up nor empties the
/// connections that answered in a moment.</para>
/// </summary>
public sealed class NetworkLookup(NetworkAccess access, TimeProvider time) : IDisposable
{
    private readonly NetworkReadings _readings = new(access, time);

    /// <summary>
    /// The suggestions for <paramref name="typed"/>, as each source answers: one list for every
    /// change, the last being the whole answer. A list that would be stale while a computer is asked
    /// is replaced by an empty one first, so the box never offers the last stage's places under the
    /// next stage's text.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<NetworkSuggestion>> SuggestAsync(
        string typed,
        ExploreLocations remembered,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remembered);

        var query = NetworkQuery.Read(typed);

        switch (query.Stage)
        {
            case NetworkQueryStage.Everything:
            case NetworkQueryStage.Computers:
                await foreach (var sources in SourcesAsync(remembered, ct).ConfigureAwait(false))
                {
                    yield return query.Stage is NetworkQueryStage.Everything
                        ? NetworkSuggestions.Everything(sources)
                        : NetworkSuggestions.Computers(sources, query.Text);
                }

                break;

            case NetworkQueryStage.Shares:
                var shares = _readings.SharesAsync(query.Parent, ct);

                if (!shares.IsCompleted)
                {
                    yield return [];
                }

                yield return NetworkSuggestions.Shares(query.Parent, await shares.ConfigureAwait(false), query.Text);

                break;

            case NetworkQueryStage.Folders:
                var folders = _readings.FoldersAsync(query.Parent, ct);

                if (!folders.IsCompleted)
                {
                    yield return [];
                }

                yield return NetworkSuggestions.Folders(query.Parent, await folders.ConfigureAwait(false), query.Text);

                break;

            default:
                yield return [];

                break;
        }
    }

    /// <summary>
    /// What <paramref name="root"/> lists. Each root reads the sources it needs together and is
    /// answered once all have answered or run out of time, because a node lists once, not in steps.
    /// </summary>
    public async Task<NetworkListing<NetworkPlace>> RootAsync(NetworkRoot root, ExploreLocations remembered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remembered);

        var sources = NetworkSources.Remembering(remembered.Recent, remembered.Computers);

        sources = root switch
        {
            NetworkRoot.MappedDrives or NetworkRoot.ConnectedShares => await ConnectionsAsync(sources, ct).ConfigureAwait(false),
            NetworkRoot.Network => sources with { Computers = await _readings.ComputersAsync(ct).ConfigureAwait(false) },
            _ => sources,
        };

        return NetworkTree.Listed(root, sources);
    }

    /// <summary>What opening <paramref name="place"/> lists: a computer's shares, or a share's or folder's folders.</summary>
    public async Task<NetworkListing<NetworkPlace>> ChildrenAsync(NetworkPlace place, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(place);

        return NetworkTree.ChildrenOf(place) is NetworkChildren.Shares
            ? NetworkTree.Shares(place, await _readings.SharesAsync(place.Name, ct).ConfigureAwait(false))
            : NetworkTree.Folders(place, await _readings.FoldersAsync(place.Path, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Sign in to <paramref name="place"/> through the Windows credentials prompt: to its share, or,
    /// for a computer that would not list its shares, to the computer itself through <c>IPC$</c>,
    /// which is how File Explorer asks for a computer's credentials.
    /// </summary>
    public Task<NetworkSignIn> SignInAsync(NetworkPlace place, nint owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(place);

        var remote = place.IsComputer ? NetworkNames.Inside(place.Path, "IPC$") : NetworkNames.ShareOf(place.Path) ?? place.Path;

        return _readings.SignInAsync(remote, owner, ct);
    }

    /// <summary>Drop what was kept about <paramref name="place"/> and everything inside it.</summary>
    public void Forget(NetworkPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        _readings.Forget(place.Path);
    }

    /// <summary>
    /// Drop what <paramref name="root"/> was listed from. The recent folders and the added computers
    /// are read from the preferences each time, so there is nothing kept for those.
    /// </summary>
    public void Forget(NetworkRoot root)
    {
        switch (root)
        {
            case NetworkRoot.MappedDrives or NetworkRoot.ConnectedShares:
                _readings.ForgetConnections();
                break;

            case NetworkRoot.Network:
                _readings.ForgetComputers();
                break;
        }
    }

    /// <summary>Drop everything kept.</summary>
    public void ForgetAll() => _readings.ForgetAll();

    public void Dispose() => _readings.Dispose();

    /// <summary>The sources an empty box reads, once with what is remembered, then again as each source answers.</summary>
    private async IAsyncEnumerable<NetworkSources> SourcesAsync(
        ExploreLocations remembered,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sources = NetworkSources.Remembering(remembered.Recent, remembered.Computers);

        yield return sources;

        var connected = _readings.ConnectedAsync(ct);
        var rememberedConnections = _readings.RememberedAsync(ct);
        var computers = _readings.ComputersAsync(ct);

        Task[] asked = [connected, rememberedConnections, computers];

        await foreach (var answered in Task.WhenEach(asked).WithCancellation(ct).ConfigureAwait(false))
        {
            // Awaited for what it throws: a wait that was cancelled ends the suggestions.
            await answered.ConfigureAwait(false);

            sources = sources with
            {
                Connected = connected.IsCompletedSuccessfully ? connected.Result : sources.Connected,
                Remembered = rememberedConnections.IsCompletedSuccessfully ? rememberedConnections.Result : sources.Remembered,
                Computers = computers.IsCompletedSuccessfully ? computers.Result : sources.Computers,
            };

            yield return sources;
        }
    }

    private async Task<NetworkSources> ConnectionsAsync(NetworkSources sources, CancellationToken ct)
    {
        var connected = _readings.ConnectedAsync(ct);
        var remembered = _readings.RememberedAsync(ct);

        return sources with { Connected = await connected.ConfigureAwait(false), Remembered = await remembered.ConfigureAwait(false) };
    }
}
