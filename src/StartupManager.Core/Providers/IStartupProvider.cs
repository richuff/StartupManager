using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// A source of startup entries (one registry key or one Startup folder).
/// Each provider knows how to enumerate, enable/disable, add and remove
/// entries it owns, and how to resolve the normalized path of a file it stores.
/// </summary>
public interface IStartupProvider
{
    EntryLocation Location { get; }

    /// <summary>True if the current process can edit this location (admin needed for HKLM / common folder).</summary>
    bool CanWrite { get; }

    IReadOnlyList<StartupEntry> Enumerate();

    void SetEnabled(StartupEntry entry, bool enabled);

    void Remove(StartupEntry entry);

    /// <summary>Create a new entry. The returned entry reflects what was written.</summary>
    StartupEntry Add(string name, string command, bool enabled);

    /// <summary>Persisted file (if any) backing this entry, used by disabled-name mapping.</summary>
    string? ResolveBackingFile(StartupEntry entry) => null;
}
