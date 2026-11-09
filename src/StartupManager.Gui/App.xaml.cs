using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

public partial class App : Application
{
    private static System.Windows.Media.ImageSource? _appIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the branded icon to every window, including dialogs and prompts.
        _appIcon = LoadAppIcon();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded));

        // Headless delayed launcher (injected as a Run entry) must not show a window.
        if (e.Args.Length >= 1 && e.Args[0] == "--delayed-launch")
        {
            StartupService.RunDelayedLauncher();
            Shutdown();
            return;
        }

        // Identity diagnostic: write token facts to a file and exit, so the GUI's
        // identity can be compared with the MCP server's without a debugger.
        // (WinExe has no console, so output goes to a file.)
        if (e.Args.Any(a => a.Equals("--whoami", StringComparison.OrdinalIgnoreCase)))
        {
            var outPath = e.Args.Length >= 2 && !e.Args[1].StartsWith("--", StringComparison.Ordinal)
                ? e.Args[1]
                : System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "startupmanager-whoami-gui.txt");
            try
            {
                System.IO.File.WriteAllText(outPath,
                    StartupManager.Core.ProcessIdentity.Describe("GUI"));
            }
            catch { /* nothing we can do headlessly */ }

            // Shutdown() is asynchronous and StartupUri would still create the main
            // window; exit hard so the diagnostic runs without flashing a UI.
            Environment.Exit(0);
            return;
        }

        // Regenerate the app icon (developer utility).
        if (e.Args.Length >= 2 && e.Args[0] == "--make-icon")
        {
            var preview = e.Args.Length >= 3 ? e.Args[2] : null;
            IconFactory.Generate(e.Args[1], preview);
            Current.Shutdown(0);
            return;
        }

        // Off-screen render-to-PNG preview (used to validate styling without a desktop session).
        if (e.Args.Length >= 2 && e.Args[0] == "--render-preview")
        {
            try
            {
                RenderPreview(e.Args[1]);
                return;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(e.Args[1] + ".err.txt", ex.ToString());
                Current.Shutdown(1);
                return;
            }
        }

        base.OnStartup(e);
    }

    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_appIcon is not null && sender is Window w && w.Icon is null)
            w.Icon = _appIcon;
    }

    /// <summary>Decode the embedded multi-size .ico for use as the window/taskbar icon.</summary>
    private static System.Windows.Media.ImageSource? LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                uri, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault();
            if (frame is null) return null;
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null; // icon is cosmetic; never block startup over it
        }
    }

    private static void RenderPreview(string path)
    {
        var window = new MainWindow();
        window.Measure(new Size(1060, 680));
        window.Arrange(new Rect(0, 0, 1060, 680));
        window.UpdateLayout();

        var bmp = new RenderTargetBitmap(1060, 680, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);

        // Diagnostic: how many entries loaded, and how many icons resolved.
        try
        {
            var vm = (MainWindowViewModel)window.DataContext;
            var all = vm.EntriesView.Cast<StartupEntry>().ToList();
            var total = all.Count;
            var withPath = all.Count(e => !string.IsNullOrEmpty(e.FilePath));
            var iconOk = 0;
            foreach (var e in all)
                if (!string.IsNullOrEmpty(e.FilePath) && ShellIcon.GetSmallIcon(e.FilePath) is not null)
                    iconOk++;
            System.IO.File.WriteAllText(path + ".diag.txt",
                $"entries={total} withPath={withPath} iconOk={iconOk}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(path + ".diag.txt", "diag error: " + ex.Message + "\n");
        }

        Current.Shutdown(0);
    }
}
