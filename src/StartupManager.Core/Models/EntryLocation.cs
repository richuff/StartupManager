namespace StartupManager.Core.Models;

/// <summary>
/// Where a startup entry lives. Registry Run/RunOnce keys and the two
/// Startup folders cover the entries a normal user can manage; Delayed
/// entries are managed by this tool itself.
/// </summary>
public enum EntryLocation
{
    CurrentUserRun,
    LocalMachineRun,
    CurrentUserRunOnce,
    LocalMachineRunOnce,
    StartupFolder,
    CommonStartupFolder,
    Delayed,
}

public static class EntryLocationExtensions
{
    public static string Describe(this EntryLocation location) => location switch
    {
        EntryLocation.CurrentUserRun => "注册表 (当前用户 Run)",
        EntryLocation.LocalMachineRun => "注册表 (本机 Run · 需管理员)",
        EntryLocation.CurrentUserRunOnce => "注册表 (当前用户 RunOnce)",
        EntryLocation.LocalMachineRunOnce => "注册表 (本机 RunOnce · 需管理员)",
        EntryLocation.StartupFolder => "启动文件夹 (当前用户)",
        EntryLocation.CommonStartupFolder => "启动文件夹 (所有用户 · 需管理员)",
        EntryLocation.Delayed => "延迟启动 (本工具管理)",
        _ => location.ToString(),
    };
}
