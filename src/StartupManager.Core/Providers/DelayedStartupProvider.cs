using System.Text.Json;
using Microsoft.Win32;
using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// Manages "delayed" startup entries entirely within this tool, with NO
/// dependency on the Windows Task Scheduler (which needs a NuGet package and
/// is unavailable offline). We store a JSON manifest under AppData and inject a
/// single self-start entry into the current user's Run key; on logon the
/// tool's delay-launcher wakes up, waits per-item, then launches each target.
/// </summary>
public class DelayedStartupProvider : IStartupProvider
{
    private static readonly string ManifestDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartupManager", "Delayed");
    private static readonly string ManifestPath = Path.Combine(ManifestDir, "manifest.json");

    public EntryLocation Location => EntryLocation.Delayed;

    public bool CanWrite => true;

    private static List<DelayedRecord> ReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new List<DelayedRecord>();
            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize<List<DelayedRecord>>(json) ?? new List<DelayedRecord>();
        }
        catch
        {
            return new List<DelayedRecord>();
        }
    }

    private static void WriteManifest(List<DelayedRecord> records)
    {
        Directory.CreateDirectory(ManifestDir);
        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ManifestPath, json);
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        return ReadManifest().OrderBy(r => r.Order).ThenBy(r => r.Name).Select(r => new StartupEntry
        {
            Id = StartupEntry.MakeId(Location, r.Name),
            Name = r.Name,
            Command = r.Command,
            FilePath = StartupEntry.SplitCommandLine(r.Command).Exe,
            Location = Location,
            Enabled = r.Enabled,
            DelaySeconds = r.DelaySeconds,
            Source = ManifestPath,
            Manufacturer = StartupEntry.ResolveManufacturer(r.Command),
        }).ToList();
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        var records = ReadManifest();
        var rec = records.FirstOrDefault(r => r.Name == entry.Name);
        if (rec is not null)
        {
            rec.Enabled = enabled;
            WriteManifest(records);
        }
    }

    public void Remove(StartupEntry entry)
    {
        var records = ReadManifest();
        records.RemoveAll(r => r.Name == entry.Name);
        WriteManifest(records);
        if (records.Count == 0) UninstallSelfStarter();
    }

    public StartupEntry Add(string name, string command, bool enabled)
    {
        var records = ReadManifest();
        var existing = records.FirstOrDefault(r => r.Name == name);
        if (existing is not null)
            throw new StartupManagerException($"名为 “{name}” 的延迟启动项已存在。");
        var (exe, _) = StartupEntry.SplitCommandLine(command);
        if (exe is null || !File.Exists(exe))
            throw new StartupManagerException($"目标可执行文件不存在或无法解析：{command}");
        records.Add(new DelayedRecord
        {
            Name = name,
            Command = command,
            Enabled = enabled,
            DelaySeconds = entry_DefaultDelay,
            Order = records.Count, // append to the end by default
        });
        WriteManifest(records);
        InstallSelfStarter();
        return new StartupEntry
        {
            Id = StartupEntry.MakeId(Location, name),
            Name = name,
            Command = command,
            FilePath = exe,
            Location = Location,
            Enabled = enabled,
            DelaySeconds = entry_DefaultDelay,
            Source = ManifestPath,
        };
    }

    private const int entry_DefaultDelay = 30;

    /// <summary>Change the delay (seconds) for an existing delayed entry.</summary>
    public void SetDelay(StartupEntry entry, int delaySeconds)
    {
        if (delaySeconds < 0) delaySeconds = 0;
        var records = ReadManifest();
        var rec = records.FirstOrDefault(r => r.Name == entry.Name);
        if (rec is not null)
        {
            rec.DelaySeconds = delaySeconds;
            WriteManifest(records);
        }
    }

    /// <summary>
    /// Called by the delay-launcher: actually start every enabled delayed entry.
    /// Each item waits its own configured delay measured from login time
    /// (absolute delay), not chained cumulatively.
    /// </summary>
    public static void LaunchAllDelayed()
    {
        var records = ReadManifest();
        var start = DateTime.UtcNow;
        foreach (var r in records.Where(r => r.Enabled))
        {
            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
            var wait = (int)(r.DelaySeconds - elapsed);
            if (wait > 0)
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            try
            {
                var (exe, args) = StartupEntry.SplitCommandLine(r.Command);
                if (exe is null) continue;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args ?? "",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Install / remove the injected self-start entry. Public so the GUI can toggle it.</summary>
    public static void SetSelfStarter(bool enabled)
    {
        if (enabled) InstallSelfStarter();
        else UninstallSelfStarter();
    }

    /// <summary>True if the self-start Run value exists and points at this tool.</summary>
    public static bool IsSelfStarterInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            var v = key?.GetValue("StartupManagerDelayed") as string;
            return !string.IsNullOrWhiteSpace(v);
        }
        catch { return false; }
    }

    /// <summary>
    /// Install a single self-start entry (current-user Run) that launches this
    /// tool's delay-launcher in headless mode.
    /// </summary>
    private static void InstallSelfStarter()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("StartupManagerDelayed",
                $"\"{SelfStarterExe()}\" --delayed-launch", RegistryValueKind.String);
        }
        catch { /* best effort */ }
    }

    private static void UninstallSelfStarter()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("StartupManagerDelayed", throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }

    /// <summary>Path to the CLI/GUI executable that hosts the delay-launcher.</summary>
    private static string SelfStarterExe()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
        return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
    }

    private class DelayedRecord
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int DelaySeconds { get; set; } = 30;
        public int Order { get; set; } = 0;
    }
}
