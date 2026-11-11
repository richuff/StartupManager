---
name: startup-manager-mcp
description: >-
  Windows 启动项与执行队列管理 MCP 工具包，把 StartupManager 的 18 个 MCP 工具
  （列出/增删/启停启动项，建立并一键启动执行队列中的所有应用）接入任意支持 stdio
  MCP 的 agent。当用户要「列出启动项」「管理开机自启」「建一个启动队列」
  「一键启动队列里所有程序」时使用。
when_to_use: |
  - 用户想查看、新增、禁用 Windows 启动项 / 开机自启项
  - 用户要创建执行队列并批量启动多个应用
  - 用户提到「启动项」「开机自启」「执行队列」「一键启动所有应用」
  - 仅支持 Windows；非 Windows 环境不要启用此 skill
tags:
  - windows
  - startup
  - autostart
  - task-queue
  - process-launch
  - mcp
  - dotnet
---

# StartupManager MCP

把本机 Windows 的启动项与执行队列管理能力通过 MCP 暴露给 agent。

> **关于分发**：本技能**只含源码**（C# / .csproj / .xaml 与说明），不含任何已编译
> 的二进制。Agensi 不允许随包分发 `.exe`/`.dll` 等可执行文件，因此服务端的
> `StartupManager.Mcp.exe` 需要你在本地用 .NET SDK 编译（步骤见下）。源码体积仅
> 数百 KB，远小于 50MB 限制。

## 平台限制

- **仅支持 Windows**。底层依赖 `net10.0-windows`、WPF 与 ShellLink COM，无法在
  macOS / Linux 运行。
- 涉及 `HKLM` 启动项或公共启动文件夹的写操作需要**管理员权限**；`HKCU` 与用户启动
  文件夹无需提权。

## 安装与构建（必需）

需要 **.NET 10 SDK**（`winget install Microsoft.DotNet.SDK.10` 或
https://dotnet.microsoft.com/download）。在仓库根目录（`StartupManager.slnx`
所在目录）执行：

```bash
# 方式 A —— 自包含单文件（目标机无需预装 .NET 运行时；推荐）
dotnet publish src/StartupManager.Mcp/StartupManager.Mcp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist

# 方式 B —— 框架依赖（目标机需已装 .NET 10 运行时），产物更小
dotnet publish src/StartupManager.Mcp/StartupManager.Mcp.csproj -c Release -r win-x64 --self-contained=false -o ./dist
```

构建产物为 `./dist/StartupManager.Mcp.exe`（方式 A 为单文件；方式 B 还会带若干
`StartupManager.*.dll`）。把 `./dist` 放到你选定的安装目录，例如
`C:\Tools\StartupManagerMcp\`。下文用 `<INSTALL_DIR>` 指代该目录。

## 注册到 agent

服务以 `stdio` 传输启动，命令指向你编译出的 exe，`args` 留空。下面是所有客户端
通用的服务端条目（**不要**再套 `mcpServers` 外壳，直接作为服务器对象使用）：

```json
"startup-manager": {
  "transport": "stdio",
  "command": "<INSTALL_DIR>\\StartupManager.Mcp.exe",
  "args": []
}
```

- **Claude Desktop**：写入 `claude_desktop_config.json`，置于顶层 `mcpServers` 对象内。
- **Cursor**：写入 `.cursor/mcp.json`，置于 `mcpServers` 对象内。
- **DSH (deepseek-harness)**：通过 MCP 客户端插件注册。在 profile 的
  `cordis.patch.yml` 里追加下面的 patch（字段为 `serverName` / `transport` /
  `command`，与上面的形态一致）：

  ```yaml
  - insert:
      - id: startup-manager-mcp
        name: '@deepseek-ai/dsh-mcp-client'
        config:
          serverName: startup-manager
          transport: stdio
          command: <INSTALL_DIR>\StartupManager.Mcp.exe
  ```

注册后工具会以 `<server>__` 前缀暴露，例如 `mcp__startup_manager__list_startup_entries`。

## 工具清单（18 个）

启动项：
1. `list_startup_entries` 列出所有启动项
2. `get_startup_entry` 查单个启动项
3. `add_startup_entry` 新增启动项
4. `remove_startup_entry` 删除启动项
5. `set_startup_enabled` 启用/禁用
6. `set_startup_delay` 设延迟秒数
7. `startup_stats` 统计
8. `delayed_status` 延迟启动器状态
9. `set_delayed_self_starter` 开关延迟自启
10. `launch_delayed_now` 立即启动延迟项

执行队列：
11. `list_queues` 列出队列
12. `add_queue` 新建队列
13. `rename_queue` 重命名
14. `remove_queue` 删除队列
15. `add_queue_item` 加应用
16. `update_queue_item` 改应用
17. `remove_queue_item` 删应用
18. `start_queue` 一键启动队列全部应用

## 用例

### 1. 列出启动项
调用 `list_startup_entries`，返回每项 `id / name / command / location / enabled / delaySeconds`。

### 2. 建一个执行队列
```
add_queue            { "name": "Morning" }
add_queue_item       { "queueId": "<id>", "name": "微信", "command": "C:\\Program Files\\WeChat\\WeChat.exe" }
add_queue_item       { "queueId": "<id>", "name": "浏览器", "command": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "delaySeconds": 3 }
```

### 3. 一键启动队列里的所有应用
```
start_queue          { "queueId": "<id>" }
```
服务在后台按各应用自己的 `delaySeconds` 依次启动。

## 注意事项

- 路径用双反斜杠或在 JSON 中转义；命令可带参数（写进 `command` 字符串）。
- `start_queue` 后台执行，不会阻塞调用。
- 修改 `HKLM`/公共启动文件夹需管理员；失败多为权限不足（Win32=5）而非文件占用。
