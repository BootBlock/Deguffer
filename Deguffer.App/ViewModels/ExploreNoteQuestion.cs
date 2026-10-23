using Deguffer.Core.Exploring.Acting;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The question the Explore selection's note waits on: what Explore says about removing what is
/// selected, one selection at a time, answered off the window's thread.
///
/// <para>Separate from <see cref="ExploreSelection"/> because its subject is a question in flight
/// rather than what is selected (G1): which one is current, when to call one off, and when to say
/// that one is taking a while. What the answer says is <see cref="ExploreRefusalNote"/>'s, and what
/// is already known is <see cref="ExploreActions.TryNote"/>'s.</para>
/// </summary>
internal sealed class ExploreNoteQuestion
{
    /// <summary>
    /// How long the note goes on saying what it said while the answer for a new selection is worked
    /// out, before it says that it is being worked out. A path or two is answered well inside it,
    /// and saying "working" for that long and then the answer would flash the note open and shut on
    /// an ordinary click.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(150);

    private readonly ExploreActions _actions;
    private readonly Action<string?> _show;

    // Which question the note is waiting on. Bumped by every new one, so an answer that arrives
    // after another question was asked is dropped rather than shown against the wrong selection.
    private int _generation;
    private CancellationTokenSource? _asking;

    /// <param name="show">Puts a sentence on the note, or takes it off for null. Called on the asking thread.</param>
    public ExploreNoteQuestion(ExploreActions actions, Action<string?> show)
    {
        _actions = actions;
        _show = show;
    }

    /// <summary>
    /// Ask about <paramref name="items"/> in place of whatever was asked before.
    ///
    /// <para>What is already known is said at once. The rest is asked off the window's thread,
    /// because each path is a question to the machine and a list selects any number of rows at
    /// once.</para>
    /// </summary>
    public void Ask(IReadOnlyList<ExploreItem> items)
    {
        var generation = ++_generation;

        _asking?.Cancel();
        _asking = null;

        if (_actions.TryNote(items, out var note))
        {
            _show(note);
            return;
        }

        _ = AskAsync(items, generation);
    }

    /// <summary>
    /// Ask about <paramref name="items"/> off the window's thread and say what the answer is, unless
    /// something else was asked meanwhile. Says that it is asking once that takes longer than
    /// <see cref="Patience"/>.
    /// </summary>
    private async Task AskAsync(IReadOnlyList<ExploreItem> items, int generation)
    {
        using var asking = new CancellationTokenSource();

        _asking = asking;

        try
        {
            var answering = _actions.NoteAsync(items, asking.Token);

            if (await Task.WhenAny(answering, Task.Delay(Patience, asking.Token)) != answering
                && generation == _generation)
            {
                _show(ExploreRefusalNote.Asking(items.Count));
            }

            var note = await answering;

            if (generation == _generation)
            {
                _show(note);
            }
        }
        catch (OperationCanceledException)
        {
            // Something else was selected while this was asked about, and its answer is the one shown.
        }
        finally
        {
            if (ReferenceEquals(_asking, asking))
            {
                _asking = null;
            }
        }
    }
}
