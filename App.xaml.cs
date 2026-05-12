using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BeetleView;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    public static Window? MainWindow { get; private set; }
    public static string? StartupFilePath { get; private set; }

    // PublishSingleFile requirement: the WindowsAppSDK self-extracts native
    // assets to AppContext.BaseDirectory at startup; the runtime must be told
    // where to find them via this env var before any WinAppSDK code runs.
    // ModuleInitializer fires before Main, so this is the earliest safe spot.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SetWindowsAppRuntimeBaseDirectory()
    {
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);
    }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Pick up a file path from the command line so "Open With" works.
        // argv[0] is the .exe, argv[1+] are user-supplied.
        var argv = Environment.GetCommandLineArgs();
        for (int i = 1; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!string.IsNullOrWhiteSpace(a) && !a.StartsWith('-') && System.IO.File.Exists(a))
            {
                StartupFilePath = System.IO.Path.GetFullPath(a);
                break;
            }
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
