using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One section of the map appearance window that changes a part of the look its window holds, through
/// the window, so every change is shown, written and told to the maps as each of the window's own is
/// (see <see cref="MapAppearanceViewModel"/>).
/// </summary>
public abstract class MapAppearanceSection : ObservableObject
{
    /// <summary>Every property of the section a change of look can move.</summary>
    private readonly string[] _properties;

    /// <param name="window">The window whose look this section changes.</param>
    /// <param name="properties">Every property of the section a change of look can move.</param>
    private protected MapAppearanceSection(MapAppearanceViewModel window, string[] properties)
    {
        Window = window;
        _properties = properties;
    }

    /// <summary>The window whose look this section changes, and through which it changes it.</summary>
    private protected MapAppearanceViewModel Window { get; }

    /// <summary>The look as it stands.</summary>
    private protected MapLook Look => Window.Look;

    /// <summary>The look moved: tell every box of the section, so each shows it as it now stands.</summary>
    internal void Refresh()
    {
        foreach (var property in _properties)
        {
            OnPropertyChanged(property);
        }
    }
}
