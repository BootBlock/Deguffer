using System.ComponentModel;
using Deguffer.Core.Health;

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
    private static readonly PropertyChangedEventArgs HealthChanged = new(nameof(Health));
    private static readonly PropertyChangedEventArgs ConcernChanged = new(nameof(HasHealthConcern));
    private static readonly PropertyChangedEventArgs DescriptionChanged = new(nameof(Description));

    internal DriveEntry(DriveChoice choice, VolumeHealthNote? health)
    {
        Choice = choice;
        Health = health;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the volume is mounted. What the row is about, so it never changes.</summary>
    public string RootPath => Choice.RootPath;

    /// <summary>The latest reading of the volume, worded for the picker.</summary>
    public DriveChoice Choice { get; private set; }

    /// <summary>
    /// What Health last found wrong with the drives under the volume, or null where it found nothing
    /// or has not read them. Apart from <see cref="Choice"/>, because it comes from Health's reading
    /// and not from the volume's, and the two arrive at different times.
    /// </summary>
    public VolumeHealthNote? Health { get; private set; }

    public bool HasHealthConcern => Health is not null;

    /// <summary>
    /// Everything the row shows, in one sentence, for the screen reader: the volume's own
    /// <see cref="DriveChoice.Description"/>, and a drive of concern after it.
    /// </summary>
    public string Description =>
        Health is { } health ? $"{Choice.Description}. Drive health: {health.Word}. {health.Text}" : Choice.Description;

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
        PropertyChanged?.Invoke(this, DescriptionChanged);
    }

    /// <summary>Take what Health now says about the drives under the volume. Nothing is raised for the same note.</summary>
    internal void Show(VolumeHealthNote? health)
    {
        if (health == Health)
        {
            return;
        }

        Health = health;
        PropertyChanged?.Invoke(this, HealthChanged);
        PropertyChanged?.Invoke(this, ConcernChanged);
        PropertyChanged?.Invoke(this, DescriptionChanged);
    }
}
