using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Headless delayed launcher, injected as a Run entry.
        if (args.Length >= 1 && args[0] == "--delayed-launch")
        {
            StartupService.RunDelayedLauncher();
            return 0;
        }

        try
        {
            return Run(args);
        }
        catch (StartupManagerException ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未处理的错误：{ex.Message}");
            return 3;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var service = new StartupService();
        var verb = args[0].ToLowerInvariant();

        switch (verb)
        {
            case "list":
            case "ls":
                return List(service, args);

            case "stats":
            case "st":
                return Stats(service);

            case "enable":
            case "on":
                return Toggle(service, args, enabled: true);

            case "disable":
            case "off":
                return Toggle(service, args, enabled: false);

            case "remove":
            case "rm":
                return Remove(service, args);

            case "add":
                return Add(service, args);

            case "delay":
                return Delay(service, args);

            case "admin":
                Console.WriteLine(StartupService.IsAdministrator()
                    ? "当前以管理员身份运行。"
                    : "当前不是管理员身份。操作 HKLM / 公共启动文件夹需要管理员权限。");
                return 0;

            default:
                Console.Error.WriteLine($"未知命令：{verb}");
                PrintUsage();
                return 1;
        }
    }

    private static int List(StartupService service, string[] args)
    {
        var onlyDisabled = args.Contains("--disabled");
        var onlyEnabled = args.Contains("--enabled");
        var entries = service.GetAllEntries();
        foreach (var e in entries)
        {
            if (onlyDisabled && e.Enabled) continue;
            if (onlyEnabled && !e.Enabled) continue;
            var flag = e.Enabled ? "[开]" : "[关]";
            var loc = e.Location.Describe();
            var delay = e.Location == EntryLocation.Delayed ? $" (延迟 {e.DelaySeconds}s)" : "";
            Console.WriteLine($"{flag} {loc,-28} {e.Name,-24} {e.Command}{delay}");
        }
        Console.WriteLine($"\n共 {entries.Count} 项。");
        return 0;
    }

    private static int Stats(StartupService service)
    {
        var s = service.ComputeStats();
        Console.WriteLine("=== 开机自启动统计 ===");
        Console.WriteLine($"总条目数   : {s.Total}");
        Console.WriteLine($"已启用     : {s.Enabled}");
        Console.WriteLine($"已禁用     : {s.Disabled}");
        Console.WriteLine($"延迟启动   : {s.DelayedCount}");
        Console.WriteLine($"系统已运行 : {FormatSpan(s.Uptime)}");
        Console.WriteLine("\n按位置分布：");
        foreach (var kv in s.PerLocation.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key.Describe(),-26} 共 {kv.Value.Total,-3} 启用 {kv.Value.Enabled}");
        return 0;
    }

    private static int Toggle(StartupService service, string[] args, bool enabled)
    {
        if (args.Length < 2) { Console.Error.WriteLine("用法: enable|disable <name> [--location <值>]"); return 1; }
        var name = args[1];
        var location = ParseLocationFlag(args);
        var entry = Find(service, name, location);
        if (entry is null) { Console.Error.WriteLine($"找不到启动项：{name}"); return 1; }
        service.SetEnabled(entry, enabled);
        Console.WriteLine($"已将 “{name}” {(enabled ? "启用" : "禁用")}。");
        return 0;
    }

    private static int Remove(StartupService service, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("用法: remove <name> [--location <值>]"); return 1; }
        var name = args[1];
        var location = ParseLocationFlag(args);
        var entry = Find(service, name, location);
        if (entry is null) { Console.Error.WriteLine($"找不到启动项：{name}"); return 1; }
        service.Remove(entry);
        Console.WriteLine($"已删除 “{name}”。");
        return 0;
    }

    private static int Add(StartupService service, string[] args)
    {
        // add <name> <command> --location <值> [--disabled]
        if (args.Length < 3) { Console.Error.WriteLine("用法: add <name> <command> --location <值> [--disabled]"); return 1; }
        var name = args[1];
        var command = args[2];
        var location = ParseLocationFlag(args);
        if (location is null) { Console.Error.WriteLine("必须通过 --location 指定启动位置，例如 --location CurrentUserRun"); return 1; }
        var enabled = !args.Contains("--disabled");
        var entry = service.Add(location.Value, name, command, enabled);
        Console.WriteLine($"已添加 “{name}” 到 {entry.Location.Describe()} ({(enabled ? "启用" : "禁用")})。");
        return 0;
    }

    private static int Delay(StartupService service, string[] args)
    {
        // delay <name> <seconds>
        if (args.Length < 3 || !int.TryParse(args[2], out var seconds)) { Console.Error.WriteLine("用法: delay <name> <seconds>"); return 1; }
        var name = args[1];
        var entries = service.GetAllEntries();
        var entry = entries.FirstOrDefault(e => e.Location == EntryLocation.Delayed &&
                                                 string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { Console.Error.WriteLine($"找不到延迟启动项：{name}"); return 1; }
        service.SetDelay(entry, seconds);
        Console.WriteLine($"已将 “{name}” 的延迟设置为 {seconds} 秒。");
        return 0;
    }

    private static StartupEntry? Find(StartupService service, string name, EntryLocation? location)
    {
        return service.GetAllEntries()
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                                 && (!location.HasValue || e.Location == location.Value));
    }

    private static EntryLocation? ParseLocationFlag(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--location", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<EntryLocation>(args[i + 1], ignoreCase: true, out var loc))
                return loc;
        }
        return null;
    }

    private static string FormatSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}天 {ts.Hours}小时 {ts.Minutes}分";
        if (ts.TotalHours >= 1) return $"{ts.Hours}小时 {ts.Minutes}分 {ts.Seconds}秒";
        return $"{ts.Minutes}分 {ts.Seconds}秒";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("StartupManager 命令行工具");
        Console.WriteLine("用法: StartupManager.Cli <命令> [参数]\n");
        Console.WriteLine("  list [--enabled|--disabled]   列出所有启动项");
        Console.WriteLine("  stats                         显示统计与系统运行时间");
        Console.WriteLine("  enable  <name> [--location X] 启用某项");
        Console.WriteLine("  disable <name> [--location X] 禁用某项");
        Console.WriteLine("  remove  <name> [--location X] 删除某项");
        Console.WriteLine("  add <name> <command> --location <值> [--disabled]");
        Console.WriteLine("                                 新增启动项");
        Console.WriteLine("  delay <name> <seconds>        设置延迟启动项的等待秒数");
        Console.WriteLine("  admin                         检查当前是否管理员");
        Console.WriteLine("\n位置值: CurrentUserRun, LocalMachineRun, CurrentUserRunOnce,");
        Console.WriteLine("        LocalMachineRunOnce, StartupFolder, CommonStartupFolder, Delayed");
    }
}
