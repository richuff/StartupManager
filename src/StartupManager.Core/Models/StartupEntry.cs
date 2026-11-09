using System.Diagnostics;

namespace StartupManager.Core.Models;

/// <summary>
/// A single auto-start entry, normalized across registry keys and
/// the Startup folders so the UI/CLI can treat them uniformly.
/// </summary>
public class StartupEntry
{
    /// <summary>Stable id = location + "|" + name.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Full command line as stored.</summary>
    public string Command { get; init; } = "";

    /// <summary>Best-effort parsed executable path (may be null).</summary>
    public string? FilePath { get; init; }

    public EntryLocation Location { get; init; }

    public bool Enabled { get; init; }

    /// <summary>For Delayed entries: seconds to wait after logon.</summary>
    public int DelaySeconds { get; init; }

    /// <summary>Original source, e.g. registry subkey path or folder path.</summary>
    public string? Source { get; init; }

    public string? Manufacturer { get; init; }

    /// <summary>Human-readable (Chinese) description of the entry's location.</summary>
    public string LocationText => Location.Describe();

    public static string MakeId(EntryLocation location, string name) => $"{location}|{name}";

    /// <summary>Attempt to resolve the company name from the target file.</summary>
    public static string? ResolveManufacturer(string? command)
    {
        var path = TryExtractExecutable(command);
        if (path is null) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Split a command line into the executable and its arguments.</summary>
    public static (string? Exe, string? Args) SplitCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (null, null);
        var exe = TryExtractExecutable(command);
        if (exe is null) return (null, null);
        var idx = IndexOfArgument(command, exe);
        var args = idx >= 0 ? command[idx..].Trim() : "";
        return (exe, string.IsNullOrEmpty(args) ? null : args);
    }

    private static int IndexOfArgument(string command, string exe)
    {
        var trimmed = command.TrimStart();
        var quoted = trimmed.StartsWith("\"", StringComparison.Ordinal);
        if (quoted)
        {
            var close = trimmed.IndexOf('"', 1);
            if (close < 0) return -1;
            return close + 1;
        }
        return trimmed.IndexOf(' ');
    }

    public static string? TryExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close < 0) return trimmed[1..];
            return trimmed.Substring(1, close - 1);
        }
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }
}
