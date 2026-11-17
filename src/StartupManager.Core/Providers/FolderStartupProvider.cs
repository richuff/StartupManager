using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// Manages a Startup folder full of .lnk shortcut files. A shortcut present in
/// the folder is "enabled"; disabling moves it into a hidden child folder so it
/// no longer sits in the Startup folder proper. This is important: Windows runs
/// every TOP-LEVEL file in the Startup folder at logon regardless of extension,
/// so leaving an "a.lnk.disabled" file there makes Windows try to open it on
/// every boot. Keeping disabled shortcuts in a subfolder avoids that entirely
/// while still letting us enumerate and re-enable them without admin rights.
/// </summary>
public class FolderStartupProvider : IStartupProvider
{
    // Legacy suffix used by old builds that renamed shortcuts in place. We still
    // read these (and migrate them on enumerate) for backwards compatibility.
    private const string DisabledExt = ".disabled";
    // Hidden child folder holding disabled shortcuts. Windows ignores subfolders.
    private const string DisabledDirName = ".sm-disabled";

    public EntryLocation Location { get; }

    public bool CanWrite { get; }

    private readonly string _folderPath;
    private readonly string _disabledDir;

    public FolderStartupProvider(string folderPath, EntryLocation location)
    {
        _folderPath = folderPath;
        _disabledDir = Path.Combine(folderPath, DisabledDirName);
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

        // Migrate any legacy top-level ".lnk.disabled" files into the hidden
        // subfolder so Windows stops trying to open them at next logon.
        MigrateLegacyDisabled();

        // Enabled: top-level "*.lnk" shortcuts.
        foreach (var file in Directory.EnumerateFiles(_folderPath, "*.lnk"))
            list.Add(DescribeEntry(file, enabled: true));

        // Disabled: shortcuts kept in the hidden child folder (plain "*.lnk").
        if (Directory.Exists(_disabledDir))
        {
            foreach (var file in Directory.EnumerateFiles(_disabledDir, "*.lnk"))
                list.Add(DescribeEntry(file, enabled: false));
        }

        return list;
    }

    /// <summary>Move old in-place "*.lnk.disabled" files from the Startup folder
    /// root into the hidden subfolder. Best effort: never let a migration hiccup
    /// break enumeration.</summary>
    private void MigrateLegacyDisabled()
    {
        if (!CanWrite || !Directory.Exists(_folderPath)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folderPath, "*.lnk" + DisabledExt))
            {
                var fileName = Path.GetFileName(file);
                var baseName = fileName[..^( ".lnk" + DisabledExt ).Length];
                var dest = Path.Combine(_disabledDir, baseName + ".lnk");
                try
                {
                    Directory.CreateDirectory(_disabledDir);
                    // If a same-named enabled shortcut somehow exists, don't overwrite it.
                    if (File.Exists(dest)) { File.Delete(file); continue; }
                    File.Move(file, dest);
                }
                catch { /* leave it; will retry next time */ }
            }
        }
        catch { /* best effort */ }
    }

    private StartupEntry DescribeEntry(string file, bool enabled)
    {
        var fileName = Path.GetFileName(file);
        // Whether stored at top level (enabled) or in the child folder (disabled),
        // the file is always a plain "*.lnk" now.
        var entryName = fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".lnk".Length]
            : fileName;

        var read = ShellLinkHelper.Read(file);
        var target = read?.Path;
        var args = read?.Arguments;
        var command = target is null
            ? ""
            : string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
        return new StartupEntry
        {
            Id = StartupEntry.MakeId(Location, entryName),
            Name = entryName,
            Command = command,
            FilePath = target,
            Location = Location,
            Enabled = enabled,
            Source = file,
            Manufacturer = StartupEntry.ResolveManufacturer(target),
        };
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (!CanWrite)
            throw new StartupManagerException($"未写入：没有权限修改 {Location.Describe()}（{_folderPath}）。");

        var fileName = entry.Source is null ? entry.Name + ".lnk" : Path.GetFileName(entry.Source);
        var baseName = fileName.EndsWith(".lnk" + DisabledExt, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^(".lnk" + DisabledExt).Length]
            : fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".lnk".Length]
                : fileName;

        var enabledPath = Path.Combine(_folderPath, baseName + ".lnk");
        var disabledPath = Path.Combine(_disabledDir, baseName + ".lnk");
        if (enabled)
        {
            Directory.CreateDirectory(_folderPath);
            if (File.Exists(disabledPath) && !File.Exists(enabledPath))
                File.Move(disabledPath, enabledPath);
        }
        else
        {
            if (File.Exists(enabledPath))
            {
                Directory.CreateDirectory(_disabledDir);
                // Avoid clobbering an already-disabled entry with the same name.
                if (File.Exists(disabledPath)) File.Delete(enabledPath);
                else File.Move(enabledPath, disabledPath);
            }
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

        var safeName = SanitizeFileName(name);
        try
        {
            if (enabled)
                Directory.CreateDirectory(_folderPath);
            else
                Directory.CreateDirectory(_disabledDir);
        }
        catch (Exception ex)
        {
            throw new StartupManagerException($"未写入：无法创建启动文件夹 —— {ex.Message}");
        }

        var fullPath = enabled
            ? Path.Combine(_folderPath, safeName + ".lnk")
            : Path.Combine(_disabledDir, safeName + ".lnk");

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
