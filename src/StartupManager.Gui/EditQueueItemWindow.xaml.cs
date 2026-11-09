using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

/// <summary>
/// Add or edit an execution-queue item: just a name and a command.
/// Delay and enabled state are set in the grid, so the dialog stays minimal.
/// Electron-style borderless window matching the rest of the app.
/// </summary>
public partial class EditQueueItemWindow : Window
{
    public string ItemName { get; private set; } = "";
    public string Command { get; private set; } = "";

    /// <summary>True when the dialog was opened to add a new item.</summary>
    public bool IsNew { get; }

    /// <summary>Create the dialog pre-filled from an existing item.</summary>
    public EditQueueItemWindow(QueueItem item) : this(item.Name, item.Command, isNew: false) { }

    /// <summary>Create the dialog empty (or lightly pre-filled) for a new item.</summary>
    public EditQueueItemWindow(string? name = null, string? command = null, bool isNew = true)
    {
        InitializeComponent();

        IsNew = isNew;
        TitleText.Text = isNew ? "添加应用" : "修改队列项";
        NameBox.Text = name ?? "";
        CommandBox.Text = command ?? "";
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要启动的程序",
            Filter = "可执行文件 (*.exe)|*.exe|批处理 (*.bat;*.cmd)|*.bat;*.cmd|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        // Quoting keeps paths with spaces working when the command line is split.
        CommandBox.Text = $"\"{dlg.FileName}\"";

        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var command = CommandBox.Text.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(command))
        {
            MessageBox.Show("名称和命令都不能为空。", "输入有误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ItemName = name;
        Command = command;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
