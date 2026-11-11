using System.Collections.ObjectModel;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Network;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The list under the Path… box, which follows what has been typed: what this account uses or can
/// see while the box is empty, then computers, then a computer's shares, then a share's folders.
///
/// <para>What is suggested, in what order and what is left out are Core's
/// (<see cref="NetworkSuggestions"/>), and so is the reading of the network
/// (<see cref="NetworkLookup"/>). This wires them to the box: one session from the box opening to its
/// closing, so each answer is asked for once, and one lookup at a time, so a lookup for text the
/// reader has typed past is cancelled and shows nothing.</para>
/// </summary>
public sealed class ExplorePathSuggestions
{
    private readonly NetworkAccess _network;
    private readonly TimeProvider _time;
    private readonly ICurrentPreferences _preferences;

    /// <summary>The box's session: open while the box is, so what it learnt is kept until it closes.</summary>
    private NetworkLookup? _session;

    /// <summary>The lookup under way, cancelled by the next one or by the box closing.</summary>
    private CancellationTokenSource? _asking;

    /// <param name="network">The sources the suggestions are read from.</param>
    /// <param name="time">What each source's time limit is measured on.</param>
    /// <param name="preferences">Where the recent folders and the added computers are read from, as they stand when asked.</param>
    internal ExplorePathSuggestions(NetworkAccess network, TimeProvider time, ICurrentPreferences preferences)
    {
        _network = network;
        _time = time;
        _preferences = preferences;
    }

    /// <summary>The suggestions for what was last typed, brought up to date in place as each source answers.</summary>
    public ObservableCollection<PathSuggestionRow> Rows { get; } = [];

    /// <summary>Start a session as the box opens, so the network is asked from now on and kept until <see cref="Close"/>.</summary>
    public void Open()
    {
        Close();
        _session = new NetworkLookup(_network, _time);
    }

    /// <summary>End the session as the box closes: stop the lookup under way, and forget what was learnt.</summary>
    public void Close()
    {
        Stop();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// List the suggestions for <paramref name="typed"/>, stopping the lookup for what was typed
    /// before. Nothing is asked outside a session.
    /// </summary>
    /// <returns>When the last source has answered, or the lookup was stopped.</returns>
    public async Task SuggestAsync(string typed)
    {
        Stop();

        if (_session is not { } session)
        {
            return;
        }

        _asking = new CancellationTokenSource();
        var ct = _asking.Token;

        try
        {
            await foreach (var suggestions in session.SuggestAsync(typed, _preferences.Current.Locations, ct))
            {
                // Checked after each answer: an answer that lands as the reader types on is for text
                // that is no longer in the box.
                ct.ThrowIfCancellationRequested();
                LiveList.Show(Rows, [.. suggestions.Select(suggestion => new PathSuggestionRow(suggestion))], row => row.Path);
            }
        }
        catch (OperationCanceledException)
        {
            // Typed past, or the box closed. Caught here because this runs from the box's handlers,
            // where an exception escaping would end the process. The rows are left as they were: a
            // cancelled lookup changes nothing.
        }
    }

    /// <summary>
    /// Cancel the lookup under way. The source is not disposed: it has no timer to free, and the
    /// lookup it stops may still be registering its token as it is cancelled.
    /// </summary>
    private void Stop()
    {
        _asking?.Cancel();
        _asking = null;
    }
}
