using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.Mcp;

/// <summary>
/// A dependency-free MCP (Model Context Protocol) server speaking JSON-RPC 2.0
/// over stdin/stdout. Exposes the StartupManager core as MCP tools so an LLM
/// client can enumerate / add / remove / enable / delay startup entries and
/// manage + launch execution queues.
///
/// Protocol notes:
///  - stdout carries protocol frames only (one JSON object per line);
///    diagnostics go to stderr.
///  - Supports initialize / notifications/initialized / ping / tools/list / tools/call.
///
/// Command line:
///  - --store &lt;path&gt;    : queue manifest location (default %LocalAppData%).
///  - --direct-write     : overwrite the queue file in place instead of temp+move.
///                         Use when creating new files is blocked but the existing
///                         store is writable (sandbox / tightened ACL).
///  - --version          : print version to stderr and exit.
/// </summary>
public static class Program
{
    private const string ServerName = "startup-manager";
    private const string ServerVersion = "1.0.0";

    /// <summary>
    /// MCP protocol versions this server speaks. If a client requests one of these we
    /// echo it back; otherwise we fall back to the newest version we support.
    /// Covers the 2024 initial spec, the 2025-03 revision, the 2025-06 revision and
    /// the 2025-11 revision used by newer clients.
    /// </summary>
    private static readonly string[] SupportedProtocolVersions =
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        "2025-11-25",
    };

    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>Version agreed with the current client (set during initialize).</summary>
    private static string _negotiatedVersion = DefaultProtocolVersion;

    private static StartupService _service = null!;
    private static StartupService Service => _service;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"{ServerName} {ServerVersion}");
            return 0;
        }

        // Identity diagnostic: print token facts to stderr and exit, so this process's
        // user / integrity level can be compared with the GUI's.
        if (args.Any(a => a.Equals("--whoami", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(StartupManager.Core.ProcessIdentity.Describe("MCP server"));
            return 0;
        }

        var directWrite = args.Any(a =>
            a.Equals("--direct-write", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--no-atomic", StringComparison.OrdinalIgnoreCase));

        _service = new StartupService(ParseStoreOverride(args), directWrite);

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        Console.Error.WriteLine(
            $"[{ServerName}] MCP stdio server started (v{ServerVersion}). " +
            $"store={Service.Queues.StorePath} directWrite={Service.Queues.DirectWrite}");

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = HandleMessage(line);
                if (response is not null)
                {
                    stdout.WriteLine(response);
                    stdout.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{ServerName}] error: {ex}");
                TryWriteParseError(stdout, ex.Message);
            }
        }

        return 0;
    }

    private static string? ParseStoreOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--store", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void TryWriteParseError(StreamWriter stdout, string message)
    {
        var err = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = -32603,
                ["message"] = message,
            },
        };
        stdout.WriteLine(err.ToJsonString());
        stdout.Flush();
    }

    private static string? HandleMessage(string raw)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch
        {
            return ErrorResponse(null, -32700, "Parse error");
        }

        if (node is not JsonObject msg) return ErrorResponse(null, -32600, "Invalid Request");

        var method = msg["method"]?.GetValue<string>();
        var id = msg["id"];
        var isNotification = id is null;

        // JSON-RPC notifications (no id) get no response.
        if (isNotification && method is not null)
        {
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, InitializeResult(msg["params"] as JsonObject)),
                "tools/list" => Result(id, ToolsListResult()),
                "tools/call" => HandleToolCall(id, msg["params"] as JsonObject),
                // Newer clients probe these even when unused; reply with empty lists
                // so discovery succeeds instead of erroring out.
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list" => Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                "ping" => Result(id, new JsonObject()),
                _ => ErrorResponse(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorResponse(id, -32603, ex.Message);
        }
    }

    private static string Result(JsonNode? id, JsonNode result)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }.ToJsonString();

    private static string ErrorResponse(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString();

    /// <summary>
    /// Return true when a string looks like an MCP date-based protocol version
    /// (YYYY-MM-DD). Lets us interoperate with future clients (e.g. 2026 revisions)
    /// whose exact version we do not know yet.
    /// </summary>
    private static bool LooksLikeProtocolVersion(string? v)
        => !string.IsNullOrWhiteSpace(v)
           && v!.Length == 10
           && v[4] == '-'
           && v[7] == '-'
           && int.TryParse(v.AsSpan(0, 4), out _)
           && int.TryParse(v.AsSpan(5, 2), out _)
           && int.TryParse(v.AsSpan(8, 2), out _);

    private static JsonObject InitializeResult(JsonObject? args)
    {
        // MCP negotiation: echo the client's requested version when we can serve it,
        // otherwise fall back to our newest known version. Clients that omit the field
        // get the 2024 baseline. A well-formed but unknown version (e.g. a future
        // 2026 revision) is echoed too — the client already speaks that version, and
        // our tool surface is version-independent.
        var requested = args?["protocolVersion"]?.GetValue<string>();
        _negotiatedVersion = string.IsNullOrWhiteSpace(requested)
            ? "2024-11-05"
            : (Array.IndexOf(SupportedProtocolVersions, requested) >= 0 || LooksLikeProtocolVersion(requested))
                ? requested!
                : DefaultProtocolVersion;

        Console.Error.WriteLine(
            $"[{ServerName}] initialize: requested='{requested ?? "(none)"}' -> '{_negotiatedVersion}'");

        return new JsonObject
        {
            ["protocolVersion"] = _negotiatedVersion,
            ["capabilities"] = Capabilities(),
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
    }

    private static JsonObject Capabilities() => new()
    {
        ["tools"] = new JsonObject { ["listChanged"] = false },
        ["resources"] = new JsonObject { ["listChanged"] = false },
        ["prompts"] = new JsonObject { ["listChanged"] = false },
    };

    // ---------- tool registry ----------

    private sealed record ToolDef(string Name, string Description, JsonObject InputSchema);

    private static List<ToolDef> AllTools() => new()
    {
        new("list_startup_entries",
            "列出所有启动项（注册表 Run/RunOnce、启动文件夹、延迟启动项）。返回每个项的 id/name/command/location/enabled/delaySeconds。",
            Schema(new JsonObject())),

        new("get_startup_entry",
            "按 id 或名称查询单个启动项详情。",
            Schema(new JsonObject
            {
                ["id"] = Prop("string", "启动项 id（location|name）"),
                ["name"] = Prop("string", "启动项名称"),
            })),

        new("add_startup_entry",
            "新增一个启动项到指定启动位置。",
            Schema(new JsonObject
            {
                ["name"] = Prop("string", "显示名称"),
                ["command"] = Prop("string", "启动命令行（可含参数）"),
                ["location"] = EnumProp("启动位置", new[]
                {
                    "CurrentUserRun", "LocalMachineRun", "CurrentUserRunOnce",
                    "LocalMachineRunOnce", "StartupFolder", "CommonStartupFolder", "Delayed",
                }),
                ["enabled"] = Prop("boolean", "是否启用，默认 true"),
            }, required: new[] { "name", "command", "location" })),

        new("remove_startup_entry",
            "删除一个启动项。",
            Schema(new JsonObject
            {
                ["id"] = Prop("string", "启动项 id"),
                ["name"] = Prop("string", "启动项名称（当位置可唯一确定时）"),
                ["location"] = EnumProp("启动位置（与 name 配合定位）", new[]
                {
                    "CurrentUserRun", "LocalMachineRun", "CurrentUserRunOnce",
                    "LocalMachineRunOnce", "StartupFolder", "CommonStartupFolder", "Delayed",
                }),
            })),

        new("set_startup_enabled",
            "启用或禁用一个启动项。",
            Schema(new JsonObject
            {
                ["id"] = Prop("string", "启动项 id"),
                ["name"] = Prop("string", "启动项名称（可选，配合 location）"),
                ["location"] = EnumProp("启动位置（可选）", new[]
                {
                    "CurrentUserRun", "LocalMachineRun", "CurrentUserRunOnce",
                    "LocalMachineRunOnce", "StartupFolder", "CommonStartupFolder", "Delayed",
                }),
                ["enabled"] = Prop("boolean", "true 启用，false 禁用"),
            }, required: new[] { "enabled" })),

        new("set_startup_delay",
            "设置延迟启动项的等待秒数。",
            Schema(new JsonObject
            {
                ["id"] = Prop("string", "延迟启动项 id"),
                ["name"] = Prop("string", "延迟启动项名称"),
                ["delaySeconds"] = Prop("integer", "登录后等待的秒数"),
            }, required: new[] { "delaySeconds" })),

        new("startup_stats",
            "获取启动项统计（总数 / 启用 / 禁用 / 延迟数 / 系统运行时长）。",
            Schema(new JsonObject())),

        new("delayed_status",
            "查询延迟启动器是否已随登录自启，以及延迟项列表。",
            Schema(new JsonObject())),

        new("set_delayed_self_starter",
            "开启或关闭「随登录自动启动延迟器」。",
            Schema(new JsonObject
            {
                ["enabled"] = Prop("boolean", "true 开启，false 关闭"),
            }, required: new[] { "enabled" })),

        new("launch_delayed_now",
            "立即启动所有启用的延迟启动项（测试用，后台执行）。",
            Schema(new JsonObject())),

        new("list_queues",
            "列出所有执行队列及其应用项。",
            Schema(new JsonObject())),

        new("add_queue",
            "新建一个执行队列。",
            Schema(new JsonObject
            {
                ["name"] = Prop("string", "队列名称"),
            }, required: new[] { "name" })),

        new("rename_queue",
            "重命名队列。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
                ["name"] = Prop("string", "新名称"),
            }, required: new[] { "queueId", "name" })),

        new("remove_queue",
            "删除队列。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
            }, required: new[] { "queueId" })),

        new("add_queue_item",
            "向队列添加一个应用（按 exe 去重）。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
                ["name"] = Prop("string", "应用名称"),
                ["command"] = Prop("string", "启动命令行"),
                ["enabled"] = Prop("boolean", "是否启用，默认 true"),
                ["delaySeconds"] = Prop("integer", "启动延迟秒数，默认 0"),
            }, required: new[] { "queueId", "name", "command" })),

        new("update_queue_item",
            "修改队列中某个应用（启用状态 / 延迟秒数）。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
                ["name"] = Prop("string", "应用名称"),
                ["enabled"] = Prop("boolean", "启用状态"),
                ["delaySeconds"] = Prop("integer", "延迟秒数"),
            }, required: new[] { "queueId", "name" })),

        new("remove_queue_item",
            "从队列中删除一个应用。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
                ["name"] = Prop("string", "应用名称"),
            }, required: new[] { "queueId", "name" })),

        new("start_queue",
            "一键启动队列（后台按各自延迟依次启动）。",
            Schema(new JsonObject
            {
                ["queueId"] = Prop("string", "队列 id"),
            }, required: new[] { "queueId" })),
    };

    private static JsonObject Schema(JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return schema;
    }

    private static JsonObject Prop(string type, string description)
        => new() { ["type"] = type, ["description"] = description };

    private static JsonObject EnumProp(string description, string[] values)
        => new()
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        };

    private static JsonObject ToolsListResult()
    {
        var arr = new JsonArray();
        foreach (var t in AllTools())
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema,
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    // ---------- tool dispatch ----------

    private static string HandleToolCall(JsonNode? id, JsonObject? args)
    {
        var name = args?["name"]?.GetValue<string>() ?? "";
        var arguments = args?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            return name switch
            {
                "list_startup_entries" => Result(id, TextContent(ListEntries())),
                "get_startup_entry" => Result(id, TextContent(GetEntry(arguments))),
                "add_startup_entry" => Result(id, TextContent(AddEntry(arguments))),
                "remove_startup_entry" => Result(id, TextContent(RemoveEntry(arguments))),
                "set_startup_enabled" => Result(id, TextContent(SetEnabled(arguments))),
                "set_startup_delay" => Result(id, TextContent(SetDelay(arguments))),
                "startup_stats" => Result(id, TextContent(Stats())),
                "delayed_status" => Result(id, TextContent(DelayedStatus())),
                "set_delayed_self_starter" => Result(id, TextContent(SetSelfStarter(arguments))),
                "launch_delayed_now" => Result(id, TextContent(LaunchDelayed())),
                "list_queues" => Result(id, TextContent(ListQueues())),
                "add_queue" => Result(id, TextContent(AddQueue(arguments))),
                "rename_queue" => Result(id, TextContent(RenameQueue(arguments))),
                "remove_queue" => Result(id, TextContent(RemoveQueue(arguments))),
                "add_queue_item" => Result(id, TextContent(AddQueueItem(arguments))),
                "update_queue_item" => Result(id, TextContent(UpdateQueueItem(arguments))),
                "remove_queue_item" => Result(id, TextContent(RemoveQueueItem(arguments))),
                "start_queue" => Result(id, TextContent(StartQueue(arguments))),
                _ => ErrorResponse(id, -32602, $"Unknown tool: {name}"),
            };
        }
        catch (Exception ex)
        {
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"错误：{ex.Message}",
                }),
                ["isError"] = true,
            });
        }
    }

    private static JsonObject TextContent(string text)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
        };

    private static string Json(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    // ---------- implementations ----------

    private static string ListEntries()
    {
        var entries = Service.GetAllEntries().Select(ToDto).ToList();
        return Json(new { count = entries.Count, entries });
    }

    private static object ToDto(StartupEntry e) => new
    {
        id = e.Id,
        name = e.Name,
        command = e.Command,
        executable = e.FilePath,
        location = e.Location.ToString(),
        locationText = e.LocationText,
        enabled = e.Enabled,
        delaySeconds = e.DelaySeconds,
        manufacturer = e.Manufacturer,
    };

    private static StartupEntry Resolve(JsonObject a)
    {
        var id = a["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var hit = Service.GetAllEntries().FirstOrDefault(e => e.Id == id);
            if (hit is not null) return hit;
            throw new StartupManagerException($"找不到 id 为 {id} 的启动项。");
        }

        var name = a["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            throw new StartupManagerException("必须提供 id 或 name。");

        var locText = a["location"]?.GetValue<string>();
        var candidates = Service.GetAllEntries().Where(e => e.Name == name).ToList();
        if (!string.IsNullOrWhiteSpace(locText) && Enum.TryParse<EntryLocation>(locText, out var loc))
            candidates = candidates.Where(e => e.Location == loc).ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new StartupManagerException($"找不到名为 {name} 的启动项。"),
            _ => throw new StartupManagerException($"名称 {name} 对应多个启动项，请提供 id 或 location。"),
        };
    }

    private static string GetEntry(JsonObject a) => Json(ToDto(Resolve(a)));

    private static string AddEntry(JsonObject a)
    {
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        var command = a["command"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 command。");
        var locText = a["location"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 location。");
        if (!Enum.TryParse<EntryLocation>(locText, out var loc))
            throw new StartupManagerException($"无效的 location：{locText}");
        var enabled = a["enabled"]?.GetValue<bool>() ?? true;

        var created = Service.Add(loc, name, command, enabled);
        return Json(new { ok = true, entry = ToDto(created) });
    }

    private static string RemoveEntry(JsonObject a)
    {
        var entry = Resolve(a);
        Service.Remove(entry);
        return Json(new { ok = true, removed = entry.Id });
    }

    private static string SetEnabled(JsonObject a)
    {
        var enabled = a["enabled"]?.GetValue<bool>()
            ?? throw new StartupManagerException("缺少 enabled。");
        var entry = Resolve(a);
        Service.SetEnabled(entry, enabled);
        return Json(new { ok = true, id = entry.Id, enabled });
    }

    private static string SetDelay(JsonObject a)
    {
        var seconds = a["delaySeconds"]?.GetValue<int>()
            ?? throw new StartupManagerException("缺少 delaySeconds。");
        var entry = Resolve(a);
        Service.SetDelay(entry, seconds);
        return Json(new { ok = true, id = entry.Id, delaySeconds = seconds });
    }

    private static string Stats()
    {
        var s = Service.ComputeStats();
        var perLocation = s.PerLocation.ToDictionary(
            kv => kv.Key.ToString(),
            kv => new { total = kv.Value.Total, enabled = kv.Value.Enabled });
        return Json(new
        {
            total = s.Total,
            enabled = s.Enabled,
            disabled = s.Disabled,
            delayed = s.DelayedCount,
            uptimeSeconds = (long)s.Uptime.TotalSeconds,
            perLocation,
            isAdministrator = StartupService.IsAdministrator(),
        });
    }

    private static string DelayedStatus()
    {
        var delayed = Service.GetDelayedEntries().Select(ToDto).ToList();
        return Json(new
        {
            selfStarterInstalled = Service.IsDelayedSelfStarterInstalled(),
            count = delayed.Count,
            entries = delayed,
        });
    }

    private static string SetSelfStarter(JsonObject a)
    {
        var enabled = a["enabled"]?.GetValue<bool>()
            ?? throw new StartupManagerException("缺少 enabled。");
        Service.SetDelayedSelfStarter(enabled);
        return Json(new { ok = true, enabled });
    }

    private static string LaunchDelayed()
    {
        Service.LaunchDelayedNow();
        return Json(new { ok = true, message = "已在后台启动所有启用的延迟项。" });
    }

    private static object QueueDto(StartupQueue q) => new
    {
        queueId = q.Id,
        name = q.Name,
        itemCount = q.Items.Count,
        items = q.Items.OrderBy(i => i.Order).Select(i => new
        {
            name = i.Name,
            command = i.Command,
            enabled = i.Enabled,
            delaySeconds = i.DelaySeconds,
            order = i.Order,
        }),
    };

    private static string ListQueues()
        => Json(new { count = Service.Queues.Queues.Count, queues = Service.Queues.Queues.Select(QueueDto) });

    private static string AddQueue(JsonObject a)
    {
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        var q = Service.Queues.AddQueue(name);
        EnsureQueueSaved();
        return Json(new { ok = true, queue = QueueDto(q) });
    }

    private static string RenameQueue(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        if (Service.Queues.Queues.All(x => x.Id != queueId))
            throw new StartupManagerException($"找不到队列 {queueId}。");
        Service.Queues.RenameQueue(queueId, name);
        EnsureQueueSaved();
        return Json(new { ok = true, queueId, name });
    }

    private static string RemoveQueue(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        if (Service.Queues.Queues.All(x => x.Id != queueId))
            throw new StartupManagerException($"找不到队列 {queueId}。");
        Service.Queues.RemoveQueue(queueId);
        EnsureQueueSaved();
        return Json(new { ok = true, queueId });
    }

    private static string AddQueueItem(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        var command = a["command"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 command。");
        var enabled = a["enabled"]?.GetValue<bool>() ?? true;
        var delay = a["delaySeconds"]?.GetValue<int>() ?? 0;

        Service.Queues.AddItem(queueId, name, command, enabled, delay);
        EnsureQueueSaved();
        var q = Service.Queues.Queues.FirstOrDefault(x => x.Id == queueId)
            ?? throw new StartupManagerException($"找不到队列 {queueId}。");
        return Json(new { ok = true, queue = QueueDto(q) });
    }

    /// <summary>Surface persistence failures instead of silently reporting success.</summary>
    private static void EnsureQueueSaved()
    {
        var err = Service.Queues.LastError;
        if (string.IsNullOrEmpty(err)) return;

        var detail = Service.Queues.LastErrorDetail;
        var win32 = Service.Queues.LastWin32Error;

        var hint = win32 switch
        {
            5 => "这是「权限不足」，不是文件被占用。",
            32 or 33 => "这是「文件被其它进程占用」，稍后重试通常可恢复。",
            _ => "请根据上面的错误码判断原因。",
        };

        throw new StartupManagerException(
            $"队列已修改但写入磁盘失败：{err}\n" +
            (win32 is not null ? $"Win32 错误码：{win32}（{hint}）\n" : string.Empty) +
            (detail is not null ? $"诊断：{detail}\n" : string.Empty) +
            $"当前存储路径：{Service.Queues.StorePath}\n" +
            "可用 --store <可写路径> 指定其它位置，或改用普通桌面方式启动。");
    }

    private static string UpdateQueueItem(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        var q = Service.Queues.Queues.FirstOrDefault(x => x.Id == queueId)
            ?? throw new StartupManagerException($"找不到队列 {queueId}。");
        var item = q.Items.FirstOrDefault(i => i.Name == name)
            ?? throw new StartupManagerException($"队列中找不到应用 {name}。");

        if (a["enabled"] is JsonNode en) item.Enabled = en.GetValue<bool>();
        if (a["delaySeconds"] is JsonNode d) item.DelaySeconds = d.GetValue<int>();
        Service.Queues.UpdateItem(queueId, item);
        EnsureQueueSaved();
        return Json(new { ok = true, queue = QueueDto(q) });
    }

    private static string RemoveQueueItem(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        var name = a["name"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 name。");
        Service.Queues.RemoveItem(queueId, name);
        EnsureQueueSaved();
        var q = Service.Queues.Queues.FirstOrDefault(x => x.Id == queueId)
            ?? throw new StartupManagerException($"找不到队列 {queueId}。");
        return Json(new { ok = true, queue = QueueDto(q) });
    }

    private static string StartQueue(JsonObject a)
    {
        var queueId = a["queueId"]?.GetValue<string>()
            ?? throw new StartupManagerException("缺少 queueId。");
        var q = Service.Queues.Queues.FirstOrDefault(x => x.Id == queueId)
            ?? throw new StartupManagerException($"找不到队列 {queueId}。");
        var enabled = q.Items.Where(i => i.Enabled).OrderBy(i => i.Order).ToList();
        if (enabled.Count == 0)
            throw new StartupManagerException("该队列没有启用的应用。");

        // Describe the plan so the caller knows exactly what will run, in what order, and when.
        var plan = enabled.Select((it, idx) => new
        {
            step = idx + 1,
            of = enabled.Count,
            name = it.Name,
            command = it.Command,
            delaySeconds = it.DelaySeconds,
            atSeconds = it.DelaySeconds,
            when = it.DelaySeconds <= 0 ? "立即" : $"点击后 {it.DelaySeconds} 秒",
        }).ToList();

        Task.Run(() => QueueManager.StartQueue(q));
        return Json(new
        {
            ok = true,
            queueId,
            started = enabled.Count,
            note = "各项从点击时刻起按自身延迟依次启动（绝对延迟，互不累加）。",
            plan,
        });
    }
}
