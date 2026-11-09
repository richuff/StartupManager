using System.Collections.Generic;

namespace StartupManager.Core.Models;

/// <summary>
/// Aggregate statistics about the managed startup entries and the
/// current session uptime.
/// </summary>
public class StartupStats
{
    public int Total { get; init; }
    public int Enabled { get; init; }
    public int Disabled { get; init; }
    public int DelayedCount { get; init; }

    /// <summary>Time since the system booted.</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>Per-location breakdown (total / enabled).</summary>
    public Dictionary<EntryLocation, LocationStat> PerLocation { get; init; } = new();
}

public class LocationStat
{
    public int Total { get; set; }
    public int Enabled { get; set; }
}
