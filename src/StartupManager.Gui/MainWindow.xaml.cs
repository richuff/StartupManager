using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();
    private FileSystemWatcher? _queueWatcher;
    private System.Windows.Threading.DispatcherTimer? _queueReloadTimer;

    // System tray: closing the window hides it here instead of quitting.
    private TrayIcon? _trayIcon;
    private bool _reallyExiting;
    private System.Windows.Interop.HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // show the initial sort arrow on the sort column header
        ApplySortIndicator();

        // initial navigation: select "启动项"
        NavList.SelectedIndex = 0;
        StartupPage.Visibility = Visibility.Visible;
        DelayedPage.Visibility = Visibility.Collapsed;

        // Show the application icon in the top-left corner (decoded once in App).
        AppIconImage.Source = App.AppIcon;

        _vm.Refresh();
        SetUpQueueWatcher();
        SetUpTrayIcon();
    }

    /// <summary>Create the system-tray icon (Win32, no WinForms dependency).</summary>
    private void SetUpTrayIcon()
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var hwnd = helper.EnsureHandle();

            // Extract the packaged app.ico to a temp file for the Win32 icon API.
            var iconPath = ExtractAppIcon();

            _trayIcon = new TrayIcon(hwnd, iconPath);
            _trayIcon.ShowRequested += ShowMainWindow;
            _trayIcon.ExitRequested += ReallyExit;

            // Hook the window procedure to receive tray callback messages.
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(TrayWndProc);

            // If tray creation failed, OnClosing falls back to a normal quit.
            if (!_trayIcon.IsAvailable)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch
        {
            _trayIcon = null; // tray unavailable -> close behaves normally
        }
    }

    private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayIcon.TrayCallbackMessage)
        {
            _trayIcon?.HandleCallback(lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static string? ExtractAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "startupmanager-tray.ico");
            using var fs = System.IO.File.Create(path);
            stream.CopyTo(fs);
            return path;
        }
        catch { return null; }
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // bring to front reliably
        Topmost = false;
    }

    /// <summary>Quit for real (used by the tray "退出" item and the 编辑→退出 menu).</summary>
    private void ReallyExit()
    {
        _reallyExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Watch the queue manifest so edits made outside this process (e.g. by the MCP
    /// server) appear without a restart. Bursts of change events are debounced.
    /// </summary>
    private void SetUpQueueWatcher()
    {
        try
        {
            var file = StartupManager.Core.QueueManager.StoreFilePath;
            var dir = System.IO.Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return;

            System.IO.Directory.CreateDirectory(dir);

            _queueReloadTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _queueReloadTimer.Tick += (_, _) => ReloadQueuesFromDisk();
            _queueReloadTimer.Stop();

            _queueWatcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                               | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChanged = (_, _) => ScheduleQueueReload();
            _queueWatcher.Changed += onChanged;
            _queueWatcher.Created += onChanged;
            _queueWatcher.Renamed += (_, _) => ScheduleQueueReload();
        }
        catch
        {
            // Watching is a convenience; never let it block startup.
        }
    }

    private void ScheduleQueueReload()
    {
        // FileSystemWatcher fires several times per save; coalesce on the UI thread.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _queueReloadTimer?.Stop();
            _queueReloadTimer?.Start();
        }));
    }

    private void ReloadQueuesFromDisk()
    {
        _queueReloadTimer?.Stop();
        try
        {
            _vm.ReloadQueues(silent: false);
        }
        catch
        {
            // A half-written file simply means "retry on the next event".
        }
    }

    private void OnReloadQueues(object sender, RoutedEventArgs e) => ReloadQueuesFromDisk();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The window's X button only hides to the tray. A real quit goes through
        // ReallyExit() (tray menu / 编辑→退出), which sets _reallyExiting.
        // If the tray icon could not be created, hiding would leave an unreachable
        // ghost process, so fall back to a normal quit in that case.
        if (!_reallyExiting && _trayIcon is not null)
        {
            e.Cancel = true;
            Hide();
            try
            {
                _trayIcon.ShowBalloon("启动项管理器", "已最小化到系统托盘，双击图标可恢复窗口。");
            }
            catch { /* tray may be unavailable on some systems */ }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _queueWatcher?.Dispose();
        _queueWatcher = null;
        _queueReloadTimer?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _hwndSource?.RemoveHook(TrayWndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    /// <summary>Reflect the current sort column/direction on the DataGrid headers.</summary>
    private void ApplySortIndicator()
    {
        foreach (var col in Grid.Columns)
            col.SortDirection = col.SortMemberPath == _vm.SortColumn ? _vm.SortDir : (System.ComponentModel.ListSortDirection?)null;
    }

    private void Grid_OnSorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        // drive sorting through the view model's single sort description and
        // show an up/down arrow on the active column header.
        e.Handled = true;
        var prop = e.Column.SortMemberPath;
        if (_vm.SortColumn == prop)
            _vm.SortDir = _vm.SortDir == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;
        else
        {
            _vm.SortColumn = prop;
            _vm.SortDir = System.ComponentModel.ListSortDirection.Ascending;
        }
        ApplySortIndicator();
    }

    private StartupEntry? Selected => Grid.SelectedItem as StartupEntry
        ?? DelayGrid.SelectedItem as StartupEntry;

    private void Nav_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not System.Windows.Controls.ListBoxItem item) return;
        var tag = item.Tag as string;
        var isStartup = tag == "Startup";
        StartupPage.Visibility = isStartup ? Visibility.Visible : Visibility.Collapsed;
        DelayedPage.Visibility = tag == "Delayed" ? Visibility.Visible : Visibility.Collapsed;
        QueuePage.Visibility = tag == "Queues" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _vm.Refresh();
    private void OnExit(object sender, RoutedEventArgs e) => ReallyExit();
    private void OnAbout(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void OnShowMcp(object sender, RoutedEventArgs e)
        => new McpWindow { Owner = this }.ShowDialog();

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartupManager");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Right-clicking a row should select it before the context menu acts.</summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dep = (e.OriginalSource as DependencyObject);
        while (dep is not null and not DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            row.IsSelected = true;
    }

    private void OnEnable(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (!Selected.Enabled) _vm.Toggle(Selected);
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (Selected.Enabled) _vm.Toggle(Selected);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (MessageBox.Show($"确定要删除 “{Selected.Name}” 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
            _vm.Remove(Selected);
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var path = Selected.FilePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            MessageBox.Show("找不到该启动项对应的可执行文件。", "打开位置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Grid_OnDoubleClick(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        _vm.Toggle(Selected);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new AddWindow(_vm.Locations.ToList());
        if (dlg.ShowDialog() == true)
        {
            if (!Enum.TryParse<EntryLocation>(dlg.Location, out var loc))
            {
                MessageBox.Show("无效的启动位置。", "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _vm.Add(dlg.Name, dlg.Command, loc, dlg.Enabled);
        }
    }

    private void OnAddDelayed(object sender, RoutedEventArgs e)
    {
        var dlg = new AddWindow(_vm.Locations.ToList(),
            fixedLocation: nameof(EntryLocation.Delayed), showDelay: true);
        if (dlg.ShowDialog() == true)
        {
            try
            {
                _vm.AddDelayed(dlg.Name, dlg.Command, dlg.Enabled, dlg.DelaySeconds);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnLaunchDelayedNow(object sender, RoutedEventArgs e) => _vm.LaunchDelayedNow();

    /// <summary>Persist a delayed row's enabled/delay edits immediately.</summary>
    private void DelayGrid_OnCellEdit(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is StartupEntry entry)
            _vm.SaveDelayed(entry);
    }

    // ---- execution queue handlers ----

    private void OnAddQueue(object sender, RoutedEventArgs e)
    {
        var input = PromptInput("新建队列", "请输入队列名称");
        if (string.IsNullOrWhiteSpace(input)) return;
        _vm.AddQueue(input.Trim());
    }

    /// <summary>A tiny modal prompt (no extra framework dependency).</summary>
    private static string? PromptInput(string title, string placeholder, string? initialText = null)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var ph = new System.Windows.Controls.TextBlock
        {
            Text = placeholder,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        box.TextChanged += (_, _) => ph.Visibility =
            string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(ph);

        var ok = new Button { Content = "确定", Width = 80, Padding = new Thickness(8, 5, 8, 5) };
        var cancel = new Button { Content = "取消", Width = 80, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8, 0, 0, 0) };
        var result = new bool?(false);
        ok.Click += (_, _) => { result = true; };
        cancel.Click += (_, _) => { result = false; };

        var win = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
        };
        var border = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                Opacity = 0.25,
                ShadowDepth = 0,
            },
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 8), Foreground = System.Windows.Media.Brushes.Black, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(grid);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        stack.Children.Add(btns);
        border.Child = stack;
        win.Content = border;

        ok.Click += (_, _) => win.Close();
        cancel.Click += (_, _) => win.Close();
        win.Loaded += (_, _) => box.Focus();
        win.ShowDialog();
        return result == true ? box.Text.Trim() : null;
    }

    private void OnRemoveQueue(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedQueue is null)
        {
            MessageBox.Show("请先在左侧选择一个队列。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"确定删除队列 “{_vm.SelectedQueue.Name}” 吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.RemoveQueue(_vm.SelectedQueue.Id);
    }

    /// <summary>Rename the selected queue through a prompt (context menu).</summary>
    private void OnEditQueue(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedQueue is null)
        {
            MessageBox.Show("请先在左侧选择一个队列。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = _vm.SelectedQueue.Name;
        var input = PromptInput("编辑队列", "请输入新的队列名称", current);
        if (string.IsNullOrWhiteSpace(input)) return;

        var name = input.Trim();
        if (name == current) return;
        _vm.RenameQueue(_vm.SelectedQueue.Id, name);
    }

    private void OnStartQueue(object sender, RoutedEventArgs e) => _vm.StartSelectedQueue();

    private void OnAddQueueItem(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedQueue is null)
        {
            MessageBox.Show("请先新建并选择一个队列。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Queue items only need a name, command and delay; location is a startup-entry concept.
        var dlg = new EditQueueItemWindow(isNew: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _vm.AddQueueItem(_vm.SelectedQueue.Id, dlg.ItemName, dlg.Command,
                dlg.Enabled, dlg.DelaySeconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveQueueItem(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not StartupManager.Core.Models.QueueItem item) return;
        if (_vm.SelectedQueue is null) return;
        if (MessageBox.Show($"确定从队列中删除 “{item.Name}” 吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.RemoveQueueItem(_vm.SelectedQueue.Id, item.Name);
    }

    /// <summary>Persist a queue item's enabled/delay edits immediately.</summary>
    private void QueueGrid_OnCellEdit(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is StartupManager.Core.Models.QueueItem item)
            _vm.SaveQueueItem(item);
    }

    // ---- queue item drag & drop reordering ----

    private System.Windows.Point _dragStart;
    private int _dragFromIndex = -1;

    private void QueueGrid_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragFromIndex = -1;

        // Never start a drag from a checkbox or text box: those need normal clicks.
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase ||
                dep is System.Windows.Controls.TextBox)
                return;
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row)
            _dragFromIndex = QueueGrid.Items.IndexOf(row.Item);
    }

    private void QueueGrid_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_dragFromIndex < 0 || _dragFromIndex >= QueueGrid.Items.Count) return;

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (QueueGrid.Items[_dragFromIndex] is not StartupManager.Core.Models.QueueItem item) return;

        DragDrop.DoDragDrop(QueueGrid,
            new DataObject(typeof(StartupManager.Core.Models.QueueItem), item),
            System.Windows.DragDropEffects.Move);
        _dragFromIndex = -1;
    }

    private void QueueGrid_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StartupManager.Core.Models.QueueItem))
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueGrid_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartupManager.Core.Models.QueueItem))) return;

        if (e.Data.GetData(typeof(StartupManager.Core.Models.QueueItem))
            is not StartupManager.Core.Models.QueueItem dragged) return;

        var from = QueueGrid.Items.IndexOf(dragged);
        if (from < 0) return;

        // Dropping on empty space below the rows moves the item to the end.
        var targetRow = FindRow(e.OriginalSource as DependencyObject);
        var to = targetRow is null ? QueueGrid.Items.Count - 1 : QueueGrid.Items.IndexOf(targetRow.Item);
        if (to < 0) return;

        _vm.MoveQueueItem(from, to);
        QueueGrid.SelectedIndex = to;
        e.Handled = true;
    }

    private static DataGridRow? FindRow(DependencyObject? dep)
    {
        while (dep is not null and not DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        return dep as DataGridRow;
    }

    private void OnQueueItemMoveUp(object sender, RoutedEventArgs e) => MoveSelectedQueueItem(-1);
    private void OnQueueItemMoveDown(object sender, RoutedEventArgs e) => MoveSelectedQueueItem(+1);

    /// <summary>Edit name / command / delay / enabled state of the selected queue item.</summary>
    private void OnEditQueueItem(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not StartupManager.Core.Models.QueueItem item)
        {
            MessageBox.Show("请先在队列中选中一项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EditQueueItemWindow(item) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var error = _vm.EditQueueItem(item, dlg.ItemName, dlg.Command,
            dlg.DelaySeconds, dlg.Enabled);
        if (error is not null)
        {
            MessageBox.Show(error, "修改失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The bound item changed; force the grid to re-read the edited values.
        QueueGrid.Items.Refresh();
    }

    private void MoveSelectedQueueItem(int offset)
    {
        if (QueueGrid.SelectedItem is not StartupManager.Core.Models.QueueItem) return;

        var from = QueueGrid.SelectedIndex;
        if (from < 0) return;

        var to = from + offset;
        if (to < 0 || to >= QueueGrid.Items.Count) return;

        _vm.MoveQueueItem(from, to);
        QueueGrid.SelectedIndex = to;
    }
}

/// <summary>Electron-style modal dialog to add a new startup entry.</summary>
public partial class AddWindow : Window
{
    public string Name { get; private set; } = "";
    public string Command { get; private set; } = "";
    public string Location { get; private set; } = nameof(EntryLocation.CurrentUserRun);
    public bool Enabled { get; private set; } = true;
    public int DelaySeconds { get; private set; } = 30;

    public AddWindow(System.Collections.Generic.List<string> locations, string? fixedLocation = null, bool showDelay = false)
    {
        InitializeComponent();
        if (showDelay)
        {
            DelayLabel.Visibility = Visibility.Visible;
            DelayHost.Visibility = Visibility.Visible;
            DelayPlaceholder.Visibility = Visibility.Visible;
        }
        if (fixedLocation is not null)
        {
            // Show the Chinese description but keep the enum name as the value.
            LocationCombo.ItemsSource = new[] { new LocationItem(fixedLocation) };
            LocationCombo.SelectedIndex = 0;
            LocationCombo.IsEnabled = false;
        }
        else
        {
            LocationCombo.ItemsSource = locations.Select(l => new LocationItem(l)).ToList();
            LocationCombo.SelectedIndex = 0;
        }
    }

    /// <summary>A location combo entry: Chinese text for display, enum name for the value.</summary>
    public sealed class LocationItem
    {
        public string Value { get; }
        public string Display { get; }
        public LocationItem(string value)
        {
            Value = value;
            Display = Enum.TryParse<EntryLocation>(value, out var loc) ? loc.Describe() : value;
        }
        public override string ToString() => Display;
    }

    private void DelayBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (DelayPlaceholder is null) return;
        DelayPlaceholder.Visibility = string.IsNullOrEmpty(DelayBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Suggest a default name from the command's executable file name (without .exe).</summary>
    private void AutoNameFromCommand()
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            return; // user already typed a name
        var exe = StartupEntry.TryExtractExecutable(CommandBox.Text.Trim());
        if (string.IsNullOrEmpty(exe))
            return;
        var file = System.IO.Path.GetFileName(exe);
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file = file.Substring(0, file.Length - 4);
        if (!string.IsNullOrEmpty(file))
            NameBox.Text = file;
    }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void CommandBox_OnLostFocus(object sender, RoutedEventArgs e) => AutoNameFromCommand();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择要启动的程序",
        };
        if (dlg.ShowDialog() == true)
        {
            CommandBox.Text = $"\"{dlg.FileName}\"";
            AutoNameFromCommand();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Name = NameBox.Text.Trim();
        Command = CommandBox.Text.Trim();
        Location = (LocationCombo.SelectedItem as LocationItem)?.Value
                   ?? nameof(EntryLocation.CurrentUserRun);
        Enabled = EnabledBox.IsChecked != false;
        if (string.IsNullOrEmpty(Name) || string.IsNullOrEmpty(Command))
        {
            System.Windows.MessageBox.Show("名称和命令都不能为空。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (DelayHost.Visibility == Visibility.Visible)
        {
            var text = DelayBox.Text.Trim();
            if (text.Length == 0)
            {
                DelaySeconds = 30; // empty means "use the default"
            }
            else if (!int.TryParse(text, out var d) || d < 0)
            {
                System.Windows.MessageBox.Show("延迟秒数必须是一个非负整数。", "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else
            {
                DelaySeconds = d;
            }
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
