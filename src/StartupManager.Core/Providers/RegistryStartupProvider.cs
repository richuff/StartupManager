using Microsoft.Win32;
using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// Reads/writes a single registry Run/RunOnce subkey. Run entries are toggled
/// by moving the value into a sibling "StartupManagerDisabled" subkey (so the
/// command is preserved and the toggle is reversible, with no data loss).
/// RunOnce entries are single-shot: disabling simply deletes the value.
/// </summary>
public class RegistryStartupProvider : IStartupProvider
{
    private readonly RegistryKey _baseKey;
    private readonly string _subKey;
    private readonly string _disabledSubKey;
    private readonly bool _isRunOnce;

    public EntryLocation Location { get; }
    public bool CanWrite { get; }

    public RegistryStartupProvider(RegistryKey baseKey, string subKey, EntryLocation location, bool isRunOnce)
    {
        _baseKey = baseKey;
        _subKey = subKey;
        _disabledSubKey = subKey + "\\StartupManagerDisabled";
        _isRunOnce = isRunOnce;
        Location = location;
        CanWrite = TryCanWrite();
    }

    private bool TryCanWrite()
    {
        try
        {
            using var key = _baseKey.OpenSubKey(_subKey, writable: true);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private RegistryKey Open(string subKey, bool writable)
    {
        var key = _baseKey.OpenSubKey(subKey, writable);
        if (key is null && writable)
            key = _baseKey.CreateSubKey(subKey, writable: true);
        if (key is null)
            throw new StartupManagerException($"无法打开注册表项 {_baseKey.Name}\\{subKey}。");
        return key;
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();

        // Enabled values live in the Run/RunOnce key.
        using (var key = Open(_subKey, writable: false))
        {
            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(value)) continue;
                list.Add(MakeEntry(valueName, value, enabled: true));
            }
        }

        // Disabled Run (not RunOnce) values live in the sibling subkey.
        if (!_isRunOnce)
        {
            using var disabledKey = _baseKey.OpenSubKey(_disabledSubKey, writable: false);
            if (disabledKey is not null)
            {
                foreach (var valueName in disabledKey.GetValueNames())
                {
                    var value = disabledKey.GetValue(valueName)?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    list.Add(MakeEntry(valueName, value, enabled: false));
                }
            }
        }

        return list;
    }

    private StartupEntry MakeEntry(string name, string command, bool enabled) => new()
    {
        Id = StartupEntry.MakeId(Location, name),
        Name = name,
        Command = command,
        FilePath = StartupEntry.SplitCommandLine(command).Exe,
        Location = Location,
        Enabled = enabled,
        Source = $"{_baseKey.Name}\\{_subKey}",
        Manufacturer = StartupEntry.ResolveManufacturer(command),
    };

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (enabled == entry.Enabled) return;
        if (!CanWrite)
            throw new StartupManagerException($"没有权限修改 {Location.Describe()}，请尝试以管理员身份运行。");

        if (_isRunOnce)
        {
            // Single-shot: there is no disabled state, so disabling removes it.
            if (!enabled) Remove(entry);
            return;
        }

        if (enabled)
        {
            // Move from disabled subkey back to Run key.
            using var from = Open(_disabledSubKey, writable: true);
            var value = from.GetValue(entry.Name)?.ToString();
            if (value is not null)
            {
                using var to = Open(_subKey, writable: true);
                to.SetValue(entry.Name, value, RegistryValueKind.String);
                from.DeleteValue(entry.Name, throwOnMissingValue: false);
            }
        }
        else
        {
            // Move from Run key to disabled subkey.
            using var from = Open(_subKey, writable: true);
            var value = from.GetValue(entry.Name)?.ToString();
            if (value is not null)
            {
                using var to = Open(_disabledSubKey, writable: true);
                to.SetValue(entry.Name, value, RegistryValueKind.String);
                from.DeleteValue(entry.Name, throwOnMissingValue: false);
            }
        }
    }

    public void Remove(StartupEntry entry)
    {
        if (!CanWrite)
            throw new StartupManagerException($"没有权限修改 {Location.Describe()}，请尝试以管理员身份运行。");
        using (var key = Open(_subKey, writable: true))
            key.DeleteValue(entry.Name, throwOnMissingValue: false);
        if (!_isRunOnce)
            using (var disabled = Open(_disabledSubKey, writable: true))
                disabled.DeleteValue(entry.Name, throwOnMissingValue: false);
    }

    public StartupEntry Add(string name, string command, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new StartupManagerException("命令不能为空。");

        var targetSubKey = _isRunOnce || enabled ? _subKey : _disabledSubKey;

        // Probe the real write path before touching anything, so a permissions problem
        // produces an accurate message instead of a vague "no permission".
        EnsureWritable(targetSubKey);

        using (var key = Open(targetSubKey, writable: true))
            key.SetValue(name, command, RegistryValueKind.String);

        // Read back: a "successful" call that did not actually persist must not be reported as success.
        VerifyWritten(targetSubKey, name, command);

        return MakeEntry(name, command, enabled);
    }

    /// <summary>Open the key for write and surface an accurate reason when it is denied.</summary>
    private void EnsureWritable(string subKey)
    {
        try
        {
            using var key = _baseKey.OpenSubKey(subKey, writable: true);
            if (key is not null) return;
        }
        catch (System.Security.SecurityException)
        {
            throw new StartupManagerException(
                $"未写入：当前进程没有注册表访问权限（{_baseKey.Name}\\{subKey}）。" +
                "请用普通桌面方式启动本程序；若在受限沙箱/服务中运行，注册表写入会被拒绝。");
        }
        catch (UnauthorizedAccessException)
        {
            throw new StartupManagerException(
                $"未写入：注册表项被拒绝写入（{_baseKey.Name}\\{subKey}），请以管理员身份运行。");
        }
        catch (Exception ex)
        {
            throw new StartupManagerException(
                $"未写入：无法打开注册表项 {_baseKey.Name}\\{subKey} —— {ex.Message}");
        }

        // The key did not exist yet: try creating it, surfacing why if that fails.
        try
        {
            using var created = _baseKey.CreateSubKey(subKey, writable: true);
            if (created is not null) return;
            throw new StartupManagerException($"未写入：无法创建注册表项 {_baseKey.Name}\\{subKey}。");
        }
        catch (StartupManagerException) { throw; }
        catch (System.Security.SecurityException)
        {
            throw new StartupManagerException(
                $"未写入：当前进程没有注册表访问权限（{_baseKey.Name}\\{subKey}）。");
        }
        catch (UnauthorizedAccessException)
        {
            throw new StartupManagerException(
                $"未写入：注册表项被拒绝创建（{_baseKey.Name}\\{subKey}），请以管理员身份运行。");
        }
        catch (Exception ex)
        {
            throw new StartupManagerException(
                $"未写入：创建注册表项失败 {_baseKey.Name}\\{subKey} —— {ex.Message}");
        }
    }

    /// <summary>Confirm the value really landed; throws when the write silently didn't persist.</summary>
    private void VerifyWritten(string subKey, string name, string expectedCommand)
    {
        string? actual;
        try
        {
            using var key = _baseKey.OpenSubKey(subKey, writable: false);
            actual = key?.GetValue(name)?.ToString();
        }
        catch (Exception ex)
        {
            throw new StartupManagerException($"写入后无法校验（{ex.Message}），请手动检查注册表。");
        }

        if (actual is null)
            throw new StartupManagerException(
                $"写入未生效：注册表值 “{name}” 未出现在 {_baseKey.Name}\\{subKey}。" +
                "可能被安全软件拦截，或进程权限不足。");

        if (!string.Equals(actual, expectedCommand, StringComparison.Ordinal))
            throw new StartupManagerException(
                $"写入内容不一致：期望 “{expectedCommand}”，实际 “{actual}”。");
    }
}
