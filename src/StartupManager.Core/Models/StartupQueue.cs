using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StartupManager.Core.Models;

/// <summary>
/// One application inside an execution queue.
/// Raises PropertyChanged so the grid reflects edits (rename, delay, enabled)
/// immediately without a full reload.
/// </summary>
public class QueueItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _command = "";
    private bool _enabled = true;
    private int _delaySeconds;
    private int _order;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Command
    {
        get => _command;
        set => Set(ref _command, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public int DelaySeconds
    {
        get => _delaySeconds;
        set => Set(ref _delaySeconds, value);
    }

    public int Order
    {
        get => _order;
        set => Set(ref _order, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A named group of applications the user can launch with one click.</summary>
public class StartupQueue : INotifyPropertyChanged
{
    private string _name = "新建队列";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public ObservableCollection<QueueItem> Items { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}
