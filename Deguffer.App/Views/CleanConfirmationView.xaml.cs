using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The body of a clean's one confirmation: every row going, grouped by tier with its size, what each
/// row needs before it may go, the total, and what follows the clean.
///
/// It is a control rather than a panel built in code because the parts it shows are theme-dependent
/// — <c>Application.Current.Resources[key]</c> in C# snapshots the brushes of whatever theme is
/// current and does not follow a repaint, exactly as <see cref="Converters.TierChipStyleConverter"/>
/// records. Every claim it makes comes from the model; nothing here decides what is being deleted.
/// </summary>
public sealed partial class CleanConfirmationView : UserControl
{
    /// <summary>
    /// Assigned before InitializeComponent, so no x:Bind can evaluate against a null model whatever
    /// the framework's initialisation order does next — the same order CleanPage relies on.
    /// </summary>
    public CleanConfirmationView(CleanConfirmationViewModel model)
    {
        Model = model;
        InitializeComponent();
    }

    public CleanConfirmationViewModel Model { get; }
}
