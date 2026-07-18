using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class MainPage : UserControl
{
    public MainPage()
    {
        // Assigned before InitializeComponent so no x:Bind can ever evaluate against a null
        // view-model, whatever the framework's initialisation order does next.
        ViewModel = new MainViewModel(
            CleanupPlanner.CreateDefault(),
            UserEnvironment.Current,
            () => new ContentDialogConfirmationPrompt(XamlRoot));
        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        InitializeComponent();

        if (ElevatedRelaunch.ShouldRescanOnLaunch)
        {
            // Deferred to Loaded rather than run here: planning posts rows back through the
            // dispatcher, and starting it before the page is live would report into nothing.
            Loaded += StartRequestedRescan;
        }
    }

    public MainViewModel ViewModel { get; }

    private void StartRequestedRescan(object sender, RoutedEventArgs e)
    {
        Loaded -= StartRequestedRescan;

        if (ViewModel.PreviewCommand.CanExecute(null))
        {
            ViewModel.PreviewCommand.Execute(null);
        }
    }
}
