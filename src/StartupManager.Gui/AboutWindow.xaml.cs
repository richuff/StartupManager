using System.Windows;

namespace StartupManager.Gui;

/// <summary>Electron-style About dialog.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
