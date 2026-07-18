using Deguffer.App.Shell;
using Microsoft.UI.Xaml;

namespace Deguffer.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdrop _backdrop;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Deguffer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        _backdrop = new WindowBackdrop(this);
        _backdrop.Apply();
    }
}
