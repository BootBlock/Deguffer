using System.ComponentModel;
using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Views;

/// <summary>
/// The Storage list following a clean: as the run starts on a row, the list scrolls that row to its
/// middle and <see cref="CleanSpotlight"/> lights it, so the reader can see what the time is going on.
///
/// <para>The light belongs to the row, not to the container that happens to show it. A list recycles
/// its containers as it scrolls, so the light leaves a container that is given another row, and comes
/// back when the row is shown again. A row that is not listed, or a list that is not on screen, gets
/// no light and no scroll, and the status line still names the row.</para>
///
/// <para>The reader's preference (<see cref="IsEnabled"/>) turns the whole of it off: no scroll and no
/// light. It is read again whenever it changes, so turning it on during a clean lights the row the run
/// is on now.</para>
/// </summary>
internal sealed class CleanRowSpotlight
{
    private readonly ListView _list;
    private readonly CleanProgressViewModel _progress;
    private readonly SystemMotion _motion;

    /// <summary>How a row is brought into view: to the list's middle, gliding if motion is on.</summary>
    private readonly BringIntoViewOptions _toMiddle = new() { VerticalAlignmentRatio = 0.5 };

    /// <summary>Built on the first row lit, so a session that never cleans never builds it.</summary>
    private CleanSpotlight? _spotlight;

    /// <summary>Whether the system's look and motion are being followed, which they are only while a row is lit.</summary>
    private bool _following;

    private bool _isEnabled = true;

    public CleanRowSpotlight(ListView list, CleanProgressViewModel progress, SystemMotion motion)
    {
        _list = list;
        _progress = progress;
        _motion = motion;

        // The list and the view-model are the page's own, so these live exactly as long as it does.
        _progress.PropertyChanged += OnProgressChanged;
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.ActualThemeChanged += (_, _) => Restyle();
    }

    /// <summary>Whether the list follows a clean at all.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                Follow();
            }
        }
    }

    private void OnProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Stage is set before Row, and only ever with it, so the row's arrival covers both.
            case nameof(CleanProgressViewModel.Row):
                Follow();
                break;

            case nameof(CleanProgressViewModel.RowFraction) when _spotlight?.Host is not null:
                _spotlight.Fill(_progress.RowFraction);
                break;
        }
    }

    /// <summary>Scroll to the row the run is on and light it, or put the light out.</summary>
    private void Follow()
    {
        if (!_isEnabled || _progress.Row is not { } row || !_list.Items.Contains(row))
        {
            Extinguish();
            return;
        }

        var container = _list.ContainerFromItem(row) as FrameworkElement;

        // A row out of the realised window has no container until the list scrolls to it. Laid out at
        // once, so the row can be brought to the middle from where the jump left it.
        if (container is null && _list.Visibility == Visibility.Visible)
        {
            _list.ScrollIntoView(row);
            _list.UpdateLayout();
            container = _list.ContainerFromItem(row) as FrameworkElement;
        }

        if (container is null)
        {
            // Lit when its container is next shown. See OnContainerContentChanging.
            Extinguish();
            return;
        }

        _toMiddle.AnimationDesired = _motion.For(MotionToken.List).Travels;
        container.StartBringIntoView(_toMiddle);

        Light(container);
    }

    /// <summary>A container recycled away from the row loses the light. One given the row gains it.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (ReferenceEquals(args.ItemContainer, _spotlight?.Host))
            {
                Extinguish();
            }

            return;
        }

        if (_isEnabled && args.Item is not null && ReferenceEquals(args.Item, _progress.Row))
        {
            Light(args.ItemContainer);
        }
        else if (ReferenceEquals(args.ItemContainer, _spotlight?.Host))
        {
            Extinguish();
        }
    }

    private void Light(FrameworkElement container)
    {
        _spotlight ??= new CleanSpotlight(ElementCompositionPreview.GetElementVisual(_list).Compositor, _motion);
        _spotlight.Show(container, _progress.Stage, _progress.RowFraction, Look());
        Watch(true);
    }

    private void Extinguish()
    {
        _spotlight?.Hide();
        Watch(false);
    }

    /// <summary>
    /// Follow the system's look and motion while a row is lit, and only then: both are events on
    /// process-wide objects, which would otherwise hold this page for the life of the process.
    /// </summary>
    private void Watch(bool follow)
    {
        if (follow == _following)
        {
            return;
        }

        _following = follow;

        if (follow)
        {
            SystemLook.Current.Changed += OnLookChanged;
            _motion.Changed += OnMotionChanged;
        }
        else
        {
            SystemLook.Current.Changed -= OnLookChanged;
            _motion.Changed -= OnMotionChanged;
        }
    }

    private void OnLookChanged(object? sender, SystemLookChange change)
    {
        if (change.HasFlag(SystemLookChange.Colours))
        {
            Restyle();
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => _spotlight?.Restyle();

    private void Restyle()
    {
        if (_spotlight?.Host is not null)
        {
            _spotlight.Restyle(Look());
        }
    }

    private CleanSpotlightLook Look() => CleanSpotlightLook.For(SystemLook.Current.State, _list.ActualTheme);
}
