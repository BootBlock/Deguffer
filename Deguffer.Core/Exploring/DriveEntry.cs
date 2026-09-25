using System.ComponentModel;

namespace Deguffer.Core.Exploring;

/// <summary>
/// One row of the Explore drive picker: a mount point that stays put, and the latest reading of the
/// volume behind it.
///
/// <para>A row rather than the <see cref="DriveChoice"/> itself, because a picker's selection is an
/// object. Replacing the chosen value with a newer one takes the selection away from the picker for
/// as long as the replacement takes, and a <c>ComboBox</c> opening its list in that moment asks for
/// the item at index -1 and takes the whole application down. Writing the newer reading into the row
/// that is already there leaves the picker nothing to lose.</para>
/// </summary>
public sealed class DriveEntry : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ChoiceChanged = new(nameof(Choice));

    internal DriveEntry(DriveChoice choice) => Choice = choice;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the volume is mounted. What the row is about, so it never changes.</summary>
    public string RootPath => Choice.RootPath;

    /// <summary>The latest reading of the volume, worded for the picker.</summary>
    public DriveChoice Choice { get; private set; }

    /// <summary>
    /// Take a newer reading of the same volume. Nothing is raised for one equal to the last, which is
    /// most of them: a label and a mount point do not move between two readings a minute apart.
    /// </summary>
    internal void Update(DriveChoice choice)
    {
        if (choice == Choice)
        {
            return;
        }

        Choice = choice;
        PropertyChanged?.Invoke(this, ChoiceChanged);
    }
}
