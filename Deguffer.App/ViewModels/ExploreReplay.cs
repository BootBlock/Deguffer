using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Explore page's replay of what grew (#285): which step from the earlier scan to now the map
/// shows, and a play through the steps.
///
/// <para>Separate from <see cref="ExploreGrowth"/>, which answers what grew, because this holds state
/// of its own: where the scrubber is, and whether it is playing. The decisions are Core's: what each
/// step holds (<see cref="GrowthReplay"/>), which step a play moves to next and how long it stays
/// (<see cref="GrowthReplay.Next"/>, <see cref="GrowthReplay.Pace"/>), and what the page says beside
/// it (<see cref="GrowthText"/>).</para>
///
/// <para>The replay is of the comparison, the tree and the folder on screen, and goes back to now as
/// any of them changes, so a step is never of anything but the picture the reader asked to replay.</para>
/// </summary>
public sealed partial class ExploreReplay : ObservableObject
{
    private readonly IMotionPolicy _motion;

    /// <summary>What the replay was last made for, so it is made again only when that changes.</summary>
    private (ScanGrowth? Growth, ExploreTree? Tree, int Node) _of = (null, null, -1);

    private GrowthReplay? _replay;

    /// <summary>Whether the play is moving the scrubber, rather than the reader, who stops the play by moving it.</summary>
    private bool _advancing;

    /// <summary>Whether the scrubber is being put back to now for another picture, which the page draws anyway.</summary>
    private bool _following;

    internal ExploreReplay(IMotionPolicy motion) => _motion = motion;

    /// <summary>How many steps there are from the earlier scan to now: the scrubber's far end.</summary>
    public double Steps => GrowthReplay.Steps;

    /// <summary>
    /// Where the scrubber is, from 0, the earlier scan, to <see cref="Steps"/>, now. A double because
    /// the slider bound to it writes one; read as the nearest step.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shows))]
    public partial double Step { get; set; } = GrowthReplay.Steps;

    partial void OnStepChanged(double value)
    {
        if (_following)
        {
            return;
        }

        // The reader took the scrubber from the play.
        if (!_advancing)
        {
            IsPlaying = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How far a move of the scrubber goes. See <see cref="GrowthReplay.Stride"/>.</summary>
    public double StepFrequency => GrowthReplay.Stride(_motion.For(MotionToken.Shift));

    /// <summary>Whether the folder on screen has an earlier picture to replay.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool CanReplay { get; private set; }

    /// <summary>Whether there is a comparison to replay at all, so the page offers the scrubber.</summary>
    [ObservableProperty]
    public partial bool IsOffered { get; private set; }

    /// <summary>What the page says beside the replay, or empty where there is no comparison.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    public partial bool IsPlaying { get; private set; }

    public string PlayText => IsPlaying ? "Pause" : "Play";

    /// <summary>Segoe Fluent Icons' Pause and Play.</summary>
    public string PlayGlyph => IsPlaying ? "" : "";

    /// <summary>Whether the map shows a step of the replay rather than the tree now.</summary>
    public bool Shows => Frame is not null;

    /// <summary>
    /// The step the map is to draw, or null where it draws the tree now: at the scrubber's far end, or
    /// where there is nothing to replay.
    /// </summary>
    public GrowthReplayFrame? Frame => CanReplay && _replay is { } replay && !GrowthReplay.IsNow(At) ? replay.At(At) : null;

    /// <summary>The scrubber moved, so the map is drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>The nearest step to where the scrubber is.</summary>
    private int At => (int)Math.Round(Math.Clamp(Step, 0, Steps));

    /// <summary>
    /// Replay <paramref name="growth"/> at <paramref name="node"/> of <paramref name="tree"/>, or
    /// nothing where <paramref name="growth"/> is null. Anything new puts the scrubber back to now and
    /// stops a play. Raises nothing, as the caller is drawing the new picture already.
    /// </summary>
    public void Follow(ScanGrowth? growth, ExploreTree? tree, int node)
    {
        if (ReferenceEquals(growth, _of.Growth) && ReferenceEquals(tree, _of.Tree) && node == _of.Node)
        {
            return;
        }

        if (!ReferenceEquals(growth, _of.Growth) || !ReferenceEquals(tree, _of.Tree))
        {
            _replay = GrowthReplay.Of(growth, tree);
        }

        _of = (growth, tree, node);

        _following = true;
        Step = Steps;
        _following = false;

        IsPlaying = false;
        IsOffered = _replay is not null;
        CanReplay = _replay?.Recorded(node) == true;
        Note = _replay is null ? string.Empty
            : CanReplay ? GrowthText.ReplayEstimate
            : GrowthText.ReplayNotRecorded;

        OnPropertyChanged(nameof(Shows));
    }

    /// <summary>Play on to now, or stop a play under way where it is.</summary>
    [RelayCommand(CanExecute = nameof(CanReplay))]
    private void Play()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            return;
        }

        IsPlaying = true;
        Move(GrowthReplay.PlayFrom(At));
    }

    /// <summary>
    /// Take a play on a step, for the page's clock. False once it has reached now, or stopped, and the
    /// clock can stop.
    /// </summary>
    public bool Advance()
    {
        if (!IsPlaying)
        {
            return false;
        }

        var next = GrowthReplay.Next(At, _motion.For(MotionToken.Shift));
        Move(next);

        if (GrowthReplay.IsNow(next))
        {
            IsPlaying = false;
        }

        return IsPlaying;
    }

    /// <summary>Stop a play where it is, for a page leaving the screen.</summary>
    public void Pause() => IsPlaying = false;

    /// <summary>How long a play stays on the step it is on. See <see cref="GrowthReplay.Pace"/>.</summary>
    public TimeSpan Pace => GrowthReplay.Pace(_motion.For(MotionToken.Shift));

    /// <summary>The reader's Animation effects setting changed, so a move of the scrubber goes another distance.</summary>
    public void Restate() => OnPropertyChanged(nameof(StepFrequency));

    private void Move(int step)
    {
        _advancing = true;
        Step = step;
        _advancing = false;
    }
}
