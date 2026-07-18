using System.Reflection;
using System.Runtime.InteropServices;
using Deguffer.App.Shell;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Deguffer.App;

public sealed partial class AboutWindow : Window
{
    private const int LogicalWidth = 460;
    private const int LogicalHeight = 560;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly WindowBackdrop _backdrop;

    public AboutWindow()
    {
        InitializeComponent();

        Title = "About Deguffer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        WindowIcon.Apply(this);

        VersionText.Text = $"Version {InformationalVersion()}";

        // AppWindow works in physical pixels, so a fixed size would come out half as large on a
        // 200% display and clip the content away entirely.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(LogicalWidth * scale), (int)(LogicalHeight * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        _backdrop = new WindowBackdrop(this);
        _backdrop.Apply();
    }

    /// <summary>
    /// The informational version carries the source-revision suffix the file version drops, so
    /// a reported version can be tied back to a commit. It is absent in a bare debug build.
    /// </summary>
    private static string InformationalVersion()
    {
        var attribute = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        return attribute?.InformationalVersion ?? "unknown";
    }
}
