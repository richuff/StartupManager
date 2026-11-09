using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

/// <summary>
/// MVVM-ish view model. We keep it dependency-free (no external MVVM package)
/// with plain INotifyPropertyChanged + ObservableCollection.
/// </summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly StartupService _service;
    private string _status = "";
    private bool _isBusy;
    private bool _delayedSelfStarterOn;

    // Source collection (unsorted / unfiltered). The grid binds to EntriesView.
    private readonly ObservableCollection<StartupEntry> _allEntries = new();
    public ICollectionView EntriesView { get; }
    public ObservableCollection<StartupEntry> DelayedEntries { get; } = new();
    public ObservableCollection<string> Locations { get; } = new();
    public ObservableCollection<string> LocationFilters { get; } = new();

    // ---- sort / filter state ----
    private string _sortColumn = "Name";
    private ListSortDirection _sortDir = ListSortDirection.Ascending;
    private string _filterText = "";
    private string _filterLocation = "全部";
    private string _filterEnabled = "全部";

    public string SortColumn
    {
        get => _sortColumn;
        set { _sortColumn = value; OnPropertyChanged(); ApplySort(); }
    }

    public ListSortDirection SortDir
    {
        get => _sortDir;
        set { _sortDir = value; OnPropertyChanged(); ApplySort(); }
    }

    public string FilterText
    {
        get => _filterText;
        set { _filterText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public string FilterLocation
    {
        get => _filterLocation;
        set { _filterLocation = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public string FilterEnabled
    {
        get => _filterEnabled;
        set { _filterEnabled = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public int VisibleCount => EntriesView.Cast<object>().Count();

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public bool IsAdmin => StartupService.IsAdministrator();

    /// <summary>Whether the delay-launcher is injected into the current user's logon.</summary>
    public bool DelayedSelfStarterOn
    {
        get => _delayedSelfStarterOn;
        set { _delayedSelfStarterOn = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- execution queues ----
    public ObservableCollection<StartupQueue> Queues { get; } = new();
    private StartupQueue? _selectedQueue;

    public StartupQueue? SelectedQueue
    {
        get => _selectedQueue;
        set
        {
            if (ReferenceEquals(_selectedQueue, value)) return;
            _selectedQueue = value;
            OnPropertyChanged();

            // Switching queues must rebuild the timeline, otherwise "执行过程" keeps
            // showing the previously selected queue's steps.
            ResetQueueRunState();
        }
    }

    public MainWindowViewModel() : this(null) { }

    /// <param name="queueStorePath">
    /// Optional queue manifest path; null uses the default %LocalAppData% store.
    /// Passing a path keeps tests and diagnostics away from the real data.
    /// </param>
    public MainWindowViewModel(string? queueStorePath)
    {
        _service = new StartupService(queueStorePath);

        EntriesView = CollectionViewSource.GetDefaultView(_allEntries);
        EntriesView.Filter = OnFilter;
        ApplySort();
        foreach (var q in _service.Queues.Queues)
            Queues.Add(q);
    }

    /// <summary>Reload the queue list from the service (used after loading a custom store).</summary>
    public void RefreshQueues()
    {
        Queues.Clear();
        foreach (var q in _service.Queues.Queues)
            Queues.Add(q);
        SelectedQueue = Queues.FirstOrDefault();
    }

    private void ApplySort()
    {
        using (EntriesView.DeferRefresh())
        {
            EntriesView.SortDescriptions.Clear();
            EntriesView.SortDescriptions.Add(new SortDescription(_sortColumn, _sortDir));
        }
        OnPropertyChanged(nameof(VisibleCount));
    }

    private bool OnFilter(object obj)
    {
        if (obj is not StartupEntry e) return false;

        if (_filterLocation != "全部" &&
            e.Location.ToString() != _filterLocation)
            return false;

        if (_filterEnabled == "已启用" && !e.Enabled) return false;
        if (_filterEnabled == "已禁用" && e.Enabled) return false;

        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            var q = _filterText.Trim();
            var hit = (e.Name != null && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (e.Command != null && e.Command.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (e.Manufacturer != null && e.Manufacturer.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (e.Location.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hit) return false;
        }
        return true;
    }

    private void ApplyFilter()
    {
        EntriesView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
    }

    public void Refresh()
    {
        IsBusy = true;
        try
        {
            _allEntries.Clear();
            foreach (var e in _service.GetAllEntries())
                _allEntries.Add(e);

            Locations.Clear();
            foreach (var loc in Enum.GetNames<EntryLocation>())
                Locations.Add(loc);

            LocationFilters.Clear();
            LocationFilters.Add("全部");
            foreach (var loc in Enum.GetNames<EntryLocation>())
                LocationFilters.Add(loc);

            var s = _service.ComputeStats();
            Status = $"共 {s.Total} 项 · 启用 {s.Enabled} · 禁用 {s.Disabled} · 延迟 {s.DelayedCount} · 系统运行 {Format(s.Uptime)}"
                     + (IsAdmin ? "" : " · 注意：非管理员，HKLM/公共文件夹不可写");

            DelayedEntries.Clear();
            foreach (var e in _allEntries.Where(e => e.Location == EntryLocation.Delayed))
                DelayedEntries.Add(e);

            DelayedSelfStarterOn = _service.IsDelayedSelfStarterInstalled();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Status = $"刷新出错：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Toggle(StartupEntry entry)
    {
        try
        {
            _service.SetEnabled(entry, !entry.Enabled);
            Status = $"已{(entry.Enabled ? "禁用" : "启用")}：{entry.Name}";
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void Remove(StartupEntry entry)
    {
        try
        {
            _service.Remove(entry);
            Status = $"已删除：{entry.Name}";
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void Add(string name, string command, EntryLocation location, bool enabled)
    {
        try
        {
            _service.Add(location, name, command, enabled);

            // Re-read from the real store: only claim success if the entry is actually there.
            Refresh();
            var landed = _allEntries.Any(e => e.Location == location && e.Name == name);
            if (!landed)
            {
                var where = location.Describe();
                MessageBox.Show(
                    $"“{name}” 未能写入 {where}。\n\n" +
                    "写入调用没有报错，但重新读取时该条目不存在，通常是被安全软件拦截或进程权限不足。",
                    "添加未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
                Status = $"添加未生效：{name}（{where}）";
                return;
            }

            Status = $"已添加：{name}（{location.Describe()}）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void AddDelayed(string name, string command, bool enabled, int delaySeconds)
    {
        try
        {
            _service.Add(EntryLocation.Delayed, name, command, enabled);
            // the delayed provider always stores a default delay; override it now
            var entry = _service.GetDelayedEntries().FirstOrDefault(x => x.Name == name);
            if (entry is not null)
                _service.SetDelay(entry, delaySeconds);
            Status = $"已添加延迟项：{name}（{delaySeconds}s）";
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void SetDelay(StartupEntry entry, int delaySeconds)
    {
        try
        {
            _service.SetDelay(entry, delaySeconds);
            Status = $"已设置延迟：{entry.Name} = {delaySeconds}s";
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    // ---- delayed-startup helpers ----

    /// <summary>Persist a delayed entry's enabled state + delay seconds immediately.</summary>
    public void SaveDelayed(StartupEntry entry)
    {
        try
        {
            _service.SetEnabled(entry, entry.Enabled);
            _service.SetDelay(entry, entry.DelaySeconds);
            Status = $"已保存延迟项：{entry.Name}（{entry.DelaySeconds}s · {(entry.Enabled ? "启用" : "禁用")}）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void ToggleDelayedSelfStarter()
    {
        try
        {
            if (!_service.HasDelayedEntries() && !DelayedSelfStarterOn)
            {
                MessageBox.Show("还没有任何延迟启动项，请先添加。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var next = !DelayedSelfStarterOn;
            _service.SetDelayedSelfStarter(next);
            DelayedSelfStarterOn = next;
            Status = next ? "已启用「随登录启动延迟器」" : "已关闭「随登录启动延迟器」";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    public void LaunchDelayedNow()
    {
        try
        {
            Status = "已触发延迟项启动（后台进行中）…";
            // Run off the UI thread so the long Thread.Sleep-based waits don't freeze the window.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _service.LaunchDelayedNow(); }
                catch { /* best effort */ }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = ex.Message;
        }
    }

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}天{ts.Hours}时{ts.Minutes}分";
        if (ts.TotalHours >= 1) return $"{ts.Hours}时{ts.Minutes}分{ts.Seconds}秒";
        return $"{ts.Minutes}分{ts.Seconds}秒";
    }

    // ---- execution queue helpers ----

    /// <summary>
    /// Reload queues from disk so changes made by another process (e.g. the MCP server)
    /// show up in the UI. Preserves the current selection by id when possible.
    /// </summary>
    public void ReloadQueues(bool silent = true)
    {
        var selectedId = SelectedQueue?.Id;

        _service.Queues.Load();

        Queues.Clear();
        foreach (var q in _service.Queues.Queues)
            Queues.Add(q);

        SelectedQueue = (selectedId is null ? null : Queues.FirstOrDefault(x => x.Id == selectedId))
                        ?? Queues.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedQueue));
        if (!silent)
            Status = $"已从磁盘重新载入 {Queues.Count} 个队列";
    }

    public void AddQueue(string name)
    {
        var q = _service.Queues.AddQueue(name);
        Queues.Add(q);
        SelectedQueue = q;
        Status = $"已新建队列：{name}";
    }

    public void RemoveQueue(string id)
    {
        _service.Queues.RemoveQueue(id);
        var q = Queues.FirstOrDefault(x => x.Id == id);
        if (q is not null) Queues.Remove(q);
        if (SelectedQueue == q) SelectedQueue = Queues.FirstOrDefault();
        Status = "已删除队列";
    }

    public void RenameQueue(string id, string name)
    {
        _service.Queues.RenameQueue(id, name);
        var q = Queues.FirstOrDefault(x => x.Id == id);
        if (q is not null) { q.Name = name; OnPropertyChanged(nameof(Queues)); }
        Status = $"已重命名队列：{name}";
    }

    public void AddQueueItem(string queueId, string name, string command, bool enabled, int delaySeconds)
    {
        var q = Queues.FirstOrDefault(x => x.Id == queueId);
        if (q is null) return;

        // de-duplicate by executable path (case-insensitive) so the same .exe can't appear twice
        var newExe = NormalizeExe(command);
        if (newExe is not null && q.Items.Any(i => NormalizeExe(i.Command) == newExe))
        {
            Status = $"「{name}」已在队列中，未重复添加。";
            return;
        }

        // The service adds straight into the same ObservableCollection the grid is bound to,
        // so the row shows up immediately (and its own duplicate guard stays authoritative).
        _service.Queues.AddItem(queueId, name, command, enabled, delaySeconds);
        Status = $"已添加队列项：{name}（{delaySeconds}s）";
    }

    private static string? NormalizeExe(string command)
        => StartupEntry.SplitCommandLine(command).Exe?.Trim().Trim('"').ToLowerInvariant();

    public void RemoveQueueItem(string queueId, string itemName)
    {
        _service.Queues.RemoveItem(queueId, itemName);
        Status = "已删除队列项";
    }

    public void SaveQueueItem(QueueItem item)
    {
        if (SelectedQueue is null) return;
        _service.Queues.UpdateItem(SelectedQueue.Id, item);
        Status = $"已保存队列项：{item.Name}（{item.DelaySeconds}s · {(item.Enabled ? "启用" : "禁用")}）";
    }

    /// <summary>
    /// Apply a full edit (name / command / delay / enabled) to one queue item.
    /// Returns null on success, or a reason when the edit was rejected.
    /// </summary>
    public string? EditQueueItem(
        QueueItem item,
        string newName,
        string command,
        int delaySeconds,
        bool enabled)
    {
        if (SelectedQueue is null) return "请先选择一个队列。";

        var error = _service.Queues.EditItem(
            SelectedQueue.Id, item.Name, newName, command, delaySeconds, enabled);
        if (error is not null) return error;

        RebuildQueueSteps();
        Status = $"已修改队列项：{newName}（{delaySeconds}s · {(enabled ? "启用" : "禁用")}）";
        return null;
    }

    /// <summary>Reorder an item (drag &amp; drop). Order is renumbered and persisted.</summary>
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (SelectedQueue is null) return;
        if (!_service.Queues.MoveItem(SelectedQueue.Id, fromIndex, toIndex)) return;

        // Recompute the timeline so the shown execution order matches immediately.
        RebuildQueueSteps();
        Status = $"已调整执行顺序：{SelectedQueue.Name}";
    }

    // ---- queue run visualisation ----
    public ObservableCollection<QueueStepVm> QueueSteps { get; } = new();

    private bool _queueRunning;
    public bool QueueRunning
    {
        get => _queueRunning;
        private set { _queueRunning = value; OnPropertyChanged(); }
    }

    private string _queueRunSummary = "";
    public string QueueRunSummary
    {
        get => _queueRunSummary;
        private set { _queueRunSummary = value; OnPropertyChanged(); }
    }

    /// <summary>Rebuild the timeline from the queue's current enabled order.</summary>
    /// <summary>
    /// Clear per-run state and rebuild the timeline for the currently selected queue.
    /// Called whenever the selection changes so "执行过程" always describes the queue
    /// the user is looking at.
    /// </summary>
    private void ResetQueueRunState()
    {
        QueueSteps.Clear();
        QueueRunSummary = SelectedQueue is null
            ? ""
            : $"「{SelectedQueue.Name}」待执行，共 {SelectedQueue.Items.Count(i => i.Enabled)} 项。";
        QueueRunning = false;
        RebuildQueueSteps();
    }

    private void RebuildQueueSteps(List<QueueItem>? enabled = null)
    {
        if (SelectedQueue is null) return;

        enabled ??= SelectedQueue.Items
            .Where(i => i.Enabled)
            .OrderBy(i => i.Order)
            .ToList();

        QueueSteps.Clear();
        for (var i = 0; i < enabled.Count; i++)
            QueueSteps.Add(new QueueStepVm
            {
                Index = i,
                Name = enabled[i].Name,
                DelaySeconds = enabled[i].DelaySeconds,
                Total = enabled.Count,
            });
    }

    /// <summary>Launch the selected queue in the background (does not freeze the UI).</summary>
    public void StartSelectedQueue()
    {
        if (SelectedQueue is null) return;
        if (QueueRunning)
        {
            Status = "已有队列正在执行，请等待完成。";
            return;
        }
        var q = SelectedQueue;
        var enabled = q.Items.Where(i => i.Enabled).OrderBy(i => i.Order).ToList();
        if (enabled.Count == 0)
        {
            MessageBox.Show("该队列没有启用的应用。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Build the timeline up-front so the user can see every step before it runs.
        RebuildQueueSteps(enabled);

        QueueRunning = true;
        QueueRunSummary = $"准备执行「{q.Name}」，共 {enabled.Count} 项…";
        Status = $"已触发队列「{q.Name}」启动（后台进行中）…";

        // IProgress marshals each report back onto the UI thread automatically.
        var progress = new Progress<QueueManager.QueueRunProgress>(OnQueueProgress);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                QueueManager.StartQueue(q, progress);
            }
            catch (Exception ex)
            {
                ((IProgress<QueueManager.QueueRunProgress>)progress).Report(new QueueManager.QueueRunProgress
                {
                    State = QueueManager.QueueRunState.Failed,
                    Message = "执行出错：" + ex.Message,
                });
            }
        });
    }

    private void OnQueueProgress(QueueManager.QueueRunProgress p)
    {
        if (p.Message is not null)
        {
            QueueRunSummary = p.Message;
            Status = p.Message;
        }

        if (p.Step is { } step && step.Index >= 0 && step.Index < QueueSteps.Count)
        {
            var vm = QueueSteps[step.Index];
            vm.State = p.State switch
            {
                QueueManager.QueueRunState.Waiting => QueueStepState.Waiting,
                QueueManager.QueueRunState.Launching => QueueStepState.Launching,
                QueueManager.QueueRunState.Launched => QueueStepState.Launched,
                QueueManager.QueueRunState.Failed => QueueStepState.Failed,
                _ => vm.State,
            };
        }

        if (p.State == QueueManager.QueueRunState.Finished || p.State == QueueManager.QueueRunState.Failed
            && p.Step is null)
        {
            QueueRunning = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Per-step state shown in the queue timeline.</summary>
public enum QueueStepState { Pending, Waiting, Launching, Launched, Failed }

/// <summary>One row of the queue execution timeline (bindable).</summary>
public class QueueStepVm : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int DelaySeconds { get; init; }
    public int Total { get; init; }

    private QueueStepState _state = QueueStepState.Pending;
    public QueueStepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateBrush)));
        }
    }

    public string StepNo => $"{Index + 1}/{Total}";

    public string StateText => State switch
    {
        QueueStepState.Waiting => "等待中",
        QueueStepState.Launching => "启动中",
        QueueStepState.Launched => "已启动",
        QueueStepState.Failed => "失败",
        _ => "待执行",
    };

    public string StateBrush => State switch
    {
        QueueStepState.Waiting => "#FFB65C",
        QueueStepState.Launching => "#4D6BFE",
        QueueStepState.Launched => "#22A06B",
        QueueStepState.Failed => "#F53F3F",
        _ => "#8F959E",
    };

    public string DelayText => DelaySeconds <= 0 ? "立即" : $"+{DelaySeconds}s";

    public event PropertyChangedEventHandler? PropertyChanged;
}
