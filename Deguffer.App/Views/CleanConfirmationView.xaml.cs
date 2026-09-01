using Deguffer.Core.Execution;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The body of the blanket "are you sure": what is about to be removed, what it comes to, and what
/// clicking Clean does.
///
/// It is a control rather than a panel built in code because the parts it shows are theme-dependent
/// — <c>Application.Current.Resources[key]</c> in C# snapshots the brushes of whatever theme is
/// current and does not follow a repaint, exactly as <see cref="Converters.TierChipStyleConverter"/>
/// records. Every claim it makes comes from the <see cref="CleanConfirmation"/>; the sentences that
/// hold for every subject are here, and nothing here decides what is being deleted.
/// </summary>
public sealed partial class CleanConfirmationView : UserControl
{
    /// <summary>
    /// Assigned before InitializeComponent, so no x:Bind can evaluate against a null model whatever
    /// the framework's initialisation order does next — the same order CleanPage relies on.
    /// </summary>
    public CleanConfirmationView(CleanConfirmation confirmation)
    {
        Confirmation = confirmation;
        InitializeComponent();
    }

    public CleanConfirmation Confirmation { get; }
}
