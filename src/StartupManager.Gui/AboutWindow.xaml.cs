using System.Reflection;
using System.Windows;

namespace StartupManager.Gui;

/// <summary>Electron-style About dialog.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = GetVersion();
    }

    /// <summary>Read the version from the assembly so it can never drift from the csproj.</summary>
    private static string GetVersion()
    {
        var asm = typeof(AboutWindow).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit sha>" suffix that the SDK appends.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        var v = asm.GetName().Version;
        return v is null ? "1.1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
