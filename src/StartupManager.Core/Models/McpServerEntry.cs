using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StartupManager.Core.Models;

/// <summary>
/// One locally recorded MCP server: a name, a human description of what it does,
/// and a JSON configuration snippet that can be pasted straight into an MCP
/// client (the <c>mcpServers</c> block). Persisted so the user never forgets how
/// a server was wired up.
/// </summary>
public class McpServerEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _description = "";
    private string _configJson = "";
    private string _command = "";

    /// <summary>Stable id; survives renames.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>What this server is for / how it is used.</summary>
    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    /// <summary>
    /// The paste-ready mcpServers JSON snippet. Kept as free text because clients
    /// differ (command/args, url for SSE, env, etc.) and the user may paste an
    /// already-complete block from a server's README.
    /// </summary>
    public string ConfigJson
    {
        get => _configJson;
        set => Set(ref _configJson, value);
    }

    /// <summary>
    /// Optional launch command (exe path or URL). When set, the generated config
    /// snippet is built from it; when empty, <see cref="ConfigJson"/> is authoritative.
    /// </summary>
    public string Command
    {
        get => _command;
        set => Set(ref _command, value);
    }

    /// <summary>Build a standard stdio mcpServers snippet from Name + Command.</summary>
    public string BuildConfigFromCommand()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "mcp-server" : Name.Trim();
        var cmd = (Command ?? "").Trim();
        if (cmd.Length == 0) return ConfigJson;

        var escaped = cmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return
            "{\n" +
            "  \"mcpServers\": {\n" +
            $"    \"{name}\": {{\n" +
            $"      \"command\": \"{escaped}\",\n" +
            "      \"args\": []\n" +
            "    }\n" +
            "  }\n" +
            "}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
