using Microsoft.Win32;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;

namespace StartupManager.Core;

/// <summary>
/// The façade the CLI and GUI both use. It aggregates every provider, exposes
/// unified enumeration / toggle / add / remove, computes statistics, and hosts
/// the delayed-launch entry point.
/// </summary>
public class StartupService
{
    private readonly List<IStartupProvider> _providers = new();
    private readonly QueueManager _queueManager;

    public IReadOnlyList<IStartupProvider> Providers => _providers;

    public QueueManager Queues => _queueManager;

    public StartupService() : this(null) { }

    /// <param name="queueStorePath">Optional explicit queue manifest path; null uses the default %LocalAppData% store.</param>
    /// <param name="directQueueWrite">Overwrite the queue file in place instead of temp-file + move.</param>
    public StartupService(string? queueStorePath, bool directQueueWrite = false)
    {
        _queueManager = new QueueManager(
            string.IsNullOrWhiteSpace(queueStorePath) ? QueueManager.StoreFilePath : queueStorePath,
            directQueueWrite);
        _queueManager.Load();

        _providers.Add(new RegistryStartupProvider(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
            EntryLocation.CurrentUserRun, isRunOnce: false));
        _providers.Add(new RegistryStartupProvider(
            Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run",
            EntryLocation.LocalMachineRun, isRunOnce: false));
        _providers.Add(new RegistryStartupProvider(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            EntryLocation.CurrentUserRunOnce, isRunOnce: true));
        _providers.Add(new RegistryStartupProvider(
            Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            EntryLocation.LocalMachineRunOnce, isRunOnce: true));

        _providers.Add(new FolderStartupProvider(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            EntryLocation.StartupFolder));
        _providers.Add(new FolderStartupProvider(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            EntryLocation.CommonStartupFolder));

        _providers.Add(new DelayedStartupProvider());
    }

    public IReadOnlyList<StartupEntry> GetAllEntries()
    {
        // Shell COM (used by the Startup-folder provider) must run on an STA thread.
        StartupEntry[]? result = null;
        var thread = new Thread(() =>
        {
            var all = new List<StartupEntry>();
            foreach (var p in _providers)
                all.AddRange(p.Enumerate());
            result = all.OrderBy(e => e.Location).ThenBy(e => e.Name).ToArray();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result ?? Array.Empty<StartupEntry>();
    }

    public StartupStats ComputeStats()
    {
        var entries = GetAllEntries();
        var perLocation = new Dictionary<EntryLocation, LocationStat>();
        foreach (var e in entries)
        {
            if (!perLocation.TryGetValue(e.Location, out var stat))
            {
                stat = new LocationStat { Total = 0, Enabled = 0 };
                perLocation[e.Location] = stat;
            }
            stat.Total++;
            if (e.Enabled) stat.Enabled++;
        }

        var uptimeMs = Environment.TickCount64;
        return new StartupStats
        {
            Total = entries.Count,
            Enabled = entries.Count(e => e.Enabled),
            Disabled = entries.Count(e => !e.Enabled),
            DelayedCount = entries.Count(e => e.Location == EntryLocation.Delayed),
            Uptime = TimeSpan.FromMilliseconds(uptimeMs),
            PerLocation = perLocation,
        };
    }

    private IStartupProvider? ProviderFor(EntryLocation location)
        => _providers.FirstOrDefault(p => p.Location == location);

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        var provider = ProviderFor(entry.Location)
            ?? throw new StartupManagerException("找不到对应的启动位置。");
        provider.SetEnabled(entry, enabled);
    }

    public void Remove(StartupEntry entry)
    {
        var provider = ProviderFor(entry.Location)
            ?? throw new StartupManagerException("找不到对应的启动位置。");
        provider.Remove(entry);
    }

    public StartupEntry Add(EntryLocation location, string name, string command, bool enabled)
    {
        var provider = ProviderFor(location)
            ?? throw new StartupManagerException("不支持的启动位置。");
        return provider.Add(name, command, enabled);
    }

    public void SetDelay(StartupEntry entry, int delaySeconds)
    {
        if (entry.Location != EntryLocation.Delayed)
            throw new StartupManagerException("只有延迟启动项可以设置延迟。");
        var delayed = (DelayedStartupProvider)ProviderFor(EntryLocation.Delayed)!;
        delayed.SetDelay(entry, delaySeconds);
    }

    // ---- delayed-startup convenience API ----

    public IReadOnlyList<StartupEntry> GetDelayedEntries()
        => _providers.OfType<DelayedStartupProvider>().First().Enumerate();

    public bool IsDelayedSelfStarterInstalled()
        => DelayedStartupProvider.IsSelfStarterInstalled();

    public void SetDelayedSelfStarter(bool enabled)
        => DelayedStartupProvider.SetSelfStarter(enabled);

    /// <summary>True if there is at least one delayed entry.</summary>
    public bool HasDelayedEntries()
        => GetDelayedEntries().Count > 0;

    /// <summary>
    /// Launch the enabled delayed entries right now (for testing), without
    /// waiting for the next logon. Mirrors RunDelayedLauncher but callable.
    /// </summary>
    public void LaunchDelayedNow()
        => DelayedStartupProvider.LaunchAllDelayed();

    /// <summary>Headless entry used by the injected self-start Run value.</summary>
    public static void RunDelayedLauncher()
    {
        DelayedStartupProvider.LaunchAllDelayed();
    }

    /// <summary>True if the current process is elevated (administrator).</summary>
    public static bool IsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
