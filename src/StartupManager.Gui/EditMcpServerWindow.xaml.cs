using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

/// <summary>
/// Add or edit a recorded MCP server: name, description, command and the paste-ready
/// JSON config. Electron-style borderless dialog matching the queue-item dialog.
/// </summary>
public partial class EditMcpServerWindow : Window
{
    public string ServerName { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Command { get; private set; } = "";
    public string ConfigJson { get; private set; } = "";

    /// <summary>True when the dialog was opened to add a new record.</summary>
    public bool IsNew { get; }

    /// <summary>Create the dialog pre-filled from an existing record.</summary>
    public EditMcpServerWindow(McpServerEntry entry) : this(
        entry.Name, entry.Description, entry.Command, entry.ConfigJson, isNew: false) { }

    /// <summary>Create the dialog for a new record.</summary>
    public EditMcpServerWindow(
        string? name = null,
        string? description = null,
        string? command = null,
        string? configJson = null,
        bool isNew = true)
    {
        InitializeComponent();

        IsNew = isNew;
        TitleText.Text = isNew ? "新建 MCP 服务记录" : "编辑 MCP 服务记录";
        NameBox.Text = name ?? "";
        DescBox.Text = description ?? "";
        CommandBox.Text = command ?? "";
        ConfigBox.Text = configJson ?? "";
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 MCP 服务端可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        CommandBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        GenerateConfig();
    }

    /// <summary>Build the standard stdio snippet from the current name + command.</summary>
    private void GenerateConfig()
    {
        var cmd = CommandBox.Text.Trim();
        if (cmd.Length == 0)
        {
            HintText.Text = "请先填写命令 / 路径。";
            return;
        }

        ConfigBox.Text = new McpServerEntry
        {
            Name = NameBox.Text.Trim(),
            Command = cmd,
        }.BuildConfigFromCommand();
        HintText.Text = "已根据命令生成配置。";
    }

    private void OnGenerateConfig(object sender, RoutedEventArgs e) => GenerateConfig();

    private void OnCopyConfig(object sender, RoutedEventArgs e)
    {
        var text = ConfigBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            var entry = new McpServerEntry { Name = NameBox.Text.Trim(), Command = CommandBox.Text.Trim() };
            text = entry.BuildConfigFromCommand();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            HintText.Text = "没有可复制的配置。";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            HintText.Text = "配置已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("名称不能为空。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ServerName = name;
        Description = DescBox.Text.Trim();
        Command = CommandBox.Text.Trim();
        ConfigJson = ConfigBox.Text;

        // No hand-written JSON but a command was given: derive a usable snippet.
        if (string.IsNullOrWhiteSpace(ConfigJson) && Command.Length > 0)
            ConfigJson = new McpServerEntry { Name = name, Command = Command }.BuildConfigFromCommand();

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
