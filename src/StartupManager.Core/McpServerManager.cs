using System.Text;
using System.Text.Json;
using System.Threading;
using StartupManager.Core.Models;

namespace StartupManager.Core;

/// <summary>
/// Records the local MCP servers the user has wired up — name, description and the
/// paste-ready mcpServers JSON — so they are never forgotten. Persisted as JSON under
/// LocalApplicationData (no admin required), mirroring <see cref="QueueManager"/>.
/// </summary>
public class McpServerManager
{
    private static readonly string DefaultStoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartupManager", "McpServers");
    private static readonly string DefaultStorePath = Path.Combine(DefaultStoreDir, "servers.json");

    private readonly List<McpServerEntry> _servers = new();
    private readonly string _storePath;

    /// <summary>Overwrite the store in place instead of staging a temp file and moving it.</summary>
    public bool DirectWrite { get; set; }

    public McpServerManager() : this(DefaultStorePath) { }

    public McpServerManager(string storePath, bool directWrite = false)
    {
        _storePath = storePath;
        DirectWrite = directWrite;
    }

    public IReadOnlyList<McpServerEntry> Servers => _servers;

    public string StorePath => _storePath;

    public static string StoreFilePath => DefaultStorePath;

    /// <summary>Last persistence error (null when the most recent save succeeded).</summary>
    public string? LastError { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            using var fs = new FileStream(_storePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<List<McpServerEntry>>(reader.ReadToEnd());
            _servers.Clear();
            if (data is not null) _servers.AddRange(data);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpServerManager] load failed: {ex}");
        }
    }

    private const int SaveAttempts = 6;

    public void Save()
    {
        var json = JsonSerializer.Serialize(_servers, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(_storePath);

        if (DirectWrite)
        {
            WriteInPlace(json, dir, "直接写入");
            return;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < SaveAttempts; attempt++)
        {
            var tmp = Path.Combine(Path.GetTempPath(),
                $"startupmanager-mcpservers-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, _storePath, overwrite: true);

                LastError = null;
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                TryDelete(tmp);
                if (attempt < SaveAttempts - 1)
                    Thread.Sleep(40 * (attempt + 1));
            }
            catch (Exception ex)
            {
                RecordFailure("序列化/未知错误", ex);
                TryDelete(tmp);
                return;
            }
        }

        if (WriteInPlace(json, dir, "就地覆盖")) return;

        LastError ??= last?.Message;
        Console.Error.WriteLine($"[McpServerManager] save failed after {SaveAttempts} attempts. {LastErrorDetail}");
    }

    private string? LastErrorDetail;

    private bool WriteInPlace(string json, string? dir, string step)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(_storePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            fs.SetLength(0);
            var bytes = new UTF8Encoding(false).GetBytes(json);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(step, ex);
            return false;
        }
    }

    private void RecordFailure(string step, Exception ex)
    {
        LastErrorDetail = $"{ex.GetType().Name} — {ex.Message}";
        LastError = $"{step}失败：{LastErrorDetail}";
        Console.Error.WriteLine($"[McpServerManager] {step}失败 @ {_storePath}\n  {LastErrorDetail}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ---- CRUD ----

    public McpServerEntry Add(string name, string description, string command, string configJson)
    {
        var entry = new McpServerEntry
        {
            Name = name,
            Description = description,
            Command = command,
            ConfigJson = string.IsNullOrWhiteSpace(configJson) ? "" : configJson,
        };
        if (string.IsNullOrWhiteSpace(entry.ConfigJson))
            entry.ConfigJson = entry.BuildConfigFromCommand();

        _servers.Add(entry);
        Save();
        return entry;
    }

    public void Remove(string id)
    {
        _servers.RemoveAll(s => s.Id == id);
        Save();
    }

    /// <summary>Replace a record's editable fields. Returns null on success, else a reason.</summary>
    public string? Update(string id, string name, string description, string command, string configJson)
    {
        var entry = _servers.FirstOrDefault(s => s.Id == id);
        if (entry is null) return "找不到该 MCP 服务记录。";
        if (string.IsNullOrWhiteSpace(name)) return "名称不能为空。";

        // Reject a rename that would collide with a different record.
        if (_servers.Any(s => !ReferenceEquals(s, entry) &&
                              string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"已存在名为 “{name}” 的记录，请换一个名称。";

        entry.Name = name;
        entry.Description = description;
        entry.Command = command;
        entry.ConfigJson = string.IsNullOrWhiteSpace(configJson)
            ? entry.BuildConfigFromCommand()
            : configJson;
        Save();
        return null;
    }
}
