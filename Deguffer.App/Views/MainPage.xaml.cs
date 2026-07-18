using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class MainPage : UserControl
{
    public MainPage()
    {
        // Assigned before InitializeComponent so no x:Bind can ever evaluate against a null
        // view-model, whatever the framework's initialisation order does next.
        ViewModel = new MainViewModel(CleanupPlanner.CreateDefault(), UserEnvironment.Current);
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }
}
