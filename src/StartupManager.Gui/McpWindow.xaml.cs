using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace StartupManager.Gui;

/// <summary>
/// Read-only "MCP service" info dialog: shows how to wire the bundled MCP server
/// into an LLM client, plus the tool catalogue and data locations.
/// </summary>
public partial class McpWindow : Window
{
    private sealed class ToolRow
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    public McpWindow()
    {
        InitializeComponent();
        LoadInfo();
    }

    private static string ExeDir => AppContext.BaseDirectory;

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartupManager");

    private void LoadInfo()
    {
        var serverExe = Path.Combine(ExeDir, "StartupManager.Mcp.exe");
        var exists = File.Exists(serverExe);

        StatusText.Text = exists
            ? "MCP 服务端可用：已随本程序一同构建。\n协议版本 2024-11-05 · 传输方式 stdio · 工具 18 个。"
            : "未在本目录找到 StartupManager.Mcp.exe。\n请构建 src/StartupManager.Mcp 项目后再使用。";

        var exeArg = serverExe.Replace("\\", "\\\\");
        ConfigBox.Text =
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"startup-manager\": {\n" +
            $"      \"command\": \"{exeArg}\",\n" +
            "      \"args\": []\n" +
            "    }\n" +
            "  }\n" +
            "}";

        ToolList.ItemsSource = new[]
        {
            new ToolRow { Name = "list_startup_entries", Description = "列出全部启动项" },
            new ToolRow { Name = "get_startup_entry", Description = "按 id 或名称查询单个启动项" },
            new ToolRow { Name = "add_startup_entry", Description = "新增启动项到指定位置" },
            new ToolRow { Name = "remove_startup_entry", Description = "删除启动项" },
            new ToolRow { Name = "set_startup_enabled", Description = "启用或禁用启动项" },
            new ToolRow { Name = "set_startup_delay", Description = "设置延迟启动秒数" },
            new ToolRow { Name = "startup_stats", Description = "启动项统计信息" },
            new ToolRow { Name = "delayed_status", Description = "延迟器状态与延迟项" },
            new ToolRow { Name = "set_delayed_self_starter", Description = "开关随登录自动启动延迟器" },
            new ToolRow { Name = "launch_delayed_now", Description = "立即启动全部延迟项（测试）" },
            new ToolRow { Name = "list_queues", Description = "列出执行队列" },
            new ToolRow { Name = "add_queue", Description = "新建队列" },
            new ToolRow { Name = "rename_queue", Description = "重命名队列" },
            new ToolRow { Name = "remove_queue", Description = "删除队列" },
            new ToolRow { Name = "add_queue_item", Description = "向队列添加应用（按 exe 去重）" },
            new ToolRow { Name = "update_queue_item", Description = "修改队列项的启用/延迟" },
            new ToolRow { Name = "remove_queue_item", Description = "从队列删除应用" },
            new ToolRow { Name = "start_queue", Description = "一键启动队列" },
        };
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopyConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ConfigBox.Text);
            StatusText.Text = "配置已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenExeDir(object sender, RoutedEventArgs e) => OpenDir(ExeDir);

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenDir(DataDir);

    private static void OpenDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
