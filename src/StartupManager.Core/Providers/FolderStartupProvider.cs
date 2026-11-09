using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// Manages a Startup folder full of .lnk shortcut files. A shortcut present in
/// the folder is "enabled"; renaming it to ".lnk.disabled" disables it without
/// deleting anything, so toggling works without administrator rights.
/// </summary>
public class FolderStartupProvider : IStartupProvider
{
    private const string DisabledExt = ".disabled";

    public EntryLocation Location { get; }

    public bool CanWrite { get; }

    private readonly string _folderPath;

    public FolderStartupProvider(string folderPath, EntryLocation location)
    {
        _folderPath = folderPath;
        Location = location;
        CanWrite = Directory.Exists(folderPath) && HasWriteAccess(folderPath);
    }

    private static bool HasWriteAccess(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".sm_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();
        if (!Directory.Exists(_folderPath)) return list;

        // Both "*.lnk" (enabled) and "*.lnk.disabled" (disabled) must be picked up.
        // A plain "*.lnk" filter silently skips the .lnk.disabled files, which made
        // disabled entries vanish from the list.
        foreach (var file in Directory.EnumerateFiles(_folderPath, "*.lnk*"))
        {
            var fileName = Path.GetFileName(file);
            var enabled = !fileName.EndsWith(".lnk" + DisabledExt, StringComparison.OrdinalIgnoreCase);
            if (enabled && !fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;

            // Strip the suffix deliberately: GetFileNameWithoutExtension("a.lnk.disabled")
            // returns "a.lnk", so we must remove the whole ".lnk[.disabled]" tail.
            var entryName = enabled
                ? fileName[..^".lnk".Length]
                : fileName[..^( ".lnk" + DisabledExt ).Length];

            var read = ShellLinkHelper.Read(file);
            var target = read?.Path;
            var args = read?.Arguments;
            var command = target is null
                ? ""
                : string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
            list.Add(new StartupEntry
            {
                Id = StartupEntry.MakeId(Location, entryName),
                Name = entryName,
                Command = command,
                FilePath = target,
                Location = Location,
                Enabled = enabled,
                Source = file,
                Manufacturer = StartupEntry.ResolveManufacturer(target),
            });
        }
        return list;
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (!CanWrite)
            throw new StartupManagerException($"未写入：没有权限修改 {Location.Describe()}（{_folderPath}）。");

        // Derive the logical name from the actual file name. GetFileNameWithoutExtension
        // would return "a.lnk" for "a.lnk.disabled", which broke re-enabling.
        var fileName = entry.Source is null ? entry.Name + ".lnk" : Path.GetFileName(entry.Source);
        var baseName = fileName.EndsWith(".lnk" + DisabledExt, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^(".lnk" + DisabledExt).Length]
            : fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".lnk".Length]
                : fileName;

        var enabledPath = Path.Combine(_folderPath, baseName + ".lnk");
        var disabledPath = Path.Combine(_folderPath, baseName + ".lnk" + DisabledExt);
        if (enabled)
        {
            if (File.Exists(disabledPath) && !File.Exists(enabledPath)) File.Move(disabledPath, enabledPath);
        }
        else
        {
            if (File.Exists(enabledPath) && !File.Exists(disabledPath)) File.Move(enabledPath, disabledPath);
        }
    }

    public void Remove(StartupEntry entry)
    {
        if (!CanWrite)
            throw new StartupManagerException($"没有权限修改 {Location.Describe()}。");
        var file = entry.Source;
        if (file is not null && File.Exists(file))
            File.Delete(file);
    }

    public StartupEntry Add(string name, string command, bool enabled)
    {
        if (!CanWrite)
            throw new StartupManagerException(
                $"未写入：没有权限修改 {Location.Describe()}（{_folderPath}）。");
        if (string.IsNullOrWhiteSpace(command))
            throw new StartupManagerException("命令不能为空。");

        try
        {
            Directory.CreateDirectory(_folderPath);
        }
        catch (Exception ex)
        {
            throw new StartupManagerException($"未写入：无法创建启动文件夹 —— {ex.Message}");
        }

        var safeName = SanitizeFileName(name);
        var fileName = safeName + ".lnk";
        if (!enabled) fileName += DisabledExt;
        var fullPath = Path.Combine(_folderPath, fileName);

        var (exe, args) = StartupEntry.SplitCommandLine(command);
        if (exe is null || !File.Exists(exe))
            throw new StartupManagerException($"目标可执行文件不存在或无法解析：{command}");

        try
        {
            ShellLinkHelper.Write(fullPath, exe, args);
        }
        catch (Exception ex)
        {
            throw new StartupManagerException($"未写入：创建快捷方式失败 —— {ex.Message}");
        }

        // Read back so a silently-blocked write is never reported as success.
        if (!File.Exists(fullPath))
            throw new StartupManagerException(
                $"写入未生效：快捷方式 “{fullPath}” 未出现在启动文件夹。可能被安全软件拦截。");

        return new StartupEntry
        {
            Id = StartupEntry.MakeId(Location, safeName),
            Name = safeName,
            Command = command,
            FilePath = exe,
            Location = Location,
            Enabled = enabled,
            Source = fullPath,
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(cleaned)) cleaned = "StartupItem";
        return cleaned;
    }
}
