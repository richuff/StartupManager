# StartupManager — Windows 开机自启动管理器

一个面向 Windows 的、零第三方依赖的开机自启动管理工具。原生 C# / .NET 10 + WPF，
同时提供 **图形界面 (GUI)** 与 **命令行 (CLI)**。

## 功能

- 列出本机全部自启动项（跨多个位置统一展示）
- 启用 / 禁用 / 删除已有启动项
- 新增启动项到指定位置
- **延迟启动**：统一管理「登录后等待 N 秒再启动」的程序，加快开机
- 统计：总数、启用/禁用数、按位置分布、系统已运行时长

## 覆盖的启动位置

| 位置 | 枚举值 | 需要 |
| --- | --- | --- |
| 注册表 当前用户 Run | `CurrentUserRun` | 普通用户 |
| 注册表 本机 Run | `LocalMachineRun` | **管理员** |
| 注册表 当前用户 RunOnce | `CurrentUserRunOnce` | 普通用户 |
| 注册表 本机 RunOnce | `LocalMachineRunOnce` | **管理员** |
| 启动文件夹 当前用户 | `StartupFolder` | 普通用户 |
| 启动文件夹 所有用户 | `CommonStartupFolder` | **管理员** |
| 延迟启动（本工具管理） | `Delayed` | 普通用户 |

> 禁用 Run 项采用「移到同级 `StartupManagerDisabled` 子键」的方式，命令不丢失、可还原。
> 禁用启动文件夹项采用「`.lnk` 改名加 `.disabled`」的方式，同样可还原。
> RunOnce 是一次性启动，禁用即删除该值。

## 架构

```
StartupManager.slnx
├── src/StartupManager.Core     核心库（模型 + 各 Provider + 服务），无任何 NuGet 依赖
├── src/StartupManager.Cli      命令行（net10.0-windows, 无 WPF）
├── src/StartupManager.Gui      WPF 图形界面（WinExe）
└── src/StartupManager.Mcp      MCP 服务端（stdio，供 LLM 接入）
```

核心库用纯 BCL + P/Invoke 实现：
- 注册表：`Microsoft.Win32.Registry`（随 Windows Desktop 目标包，无需还原）
- 快捷方式读写：`IShellLink` / `IPersistFile` COM 互操作（无需 NuGet）
- 开机/运行时长：`Environment.TickCount64`（无需 EventLog）

## 构建

需要 .NET 10 SDK（含 Windows Desktop 工作负载，离线即可，无需联网还原）。

```bash
dotnet build StartupManager.slnx
```

## 使用

### 命令行

```bash
# 列出全部启动项
StartupManager.Cli list

# 仅列出已禁用 / 仅已启用
StartupManager.Cli list --disabled
StartupManager.Cli list --enabled

# 统计
StartupManager.Cli stats

# 启用 / 禁用 / 删除（同名项可用 --location 区分）
StartupManager.Cli disable "OneDrive" --location CurrentUserRun
StartupManager.Cli enable  "OneDrive" --location CurrentUserRun
StartupManager.Cli remove  "OldApp"  --location CurrentUserRun

# 新增启动项（--disabled 可加可不加；location 必填）
StartupManager.Cli add "MyApp" "\"C:\Path\app.exe\" --minimized" --location CurrentUserRun

# 延迟启动项的等待秒数
StartupManager.Cli delay "MyApp" 60

# 检查当前是否管理员
StartupManager.Cli admin
```

### 图形界面

直接运行 `StartupManager.Gui.exe`：
- 「启动项」页：双击行切换启用/禁用；工具栏可启用/禁用/删除/添加
- 「延迟启动」页：调整每项延迟秒数，点击「应用延迟改动」

## 关于延迟启动的实现

延迟启动完全由本工具自管理，**不依赖 Windows 任务计划程序**（离线环境无法引用
`System.Diagnostics.EventLog` / 任务计划程序 COM 包）。做法是：
本工具向当前用户 Run 注入一条自启动项 `StartupManagerDelayed`，
登录后 `--delayed-launch` 无窗口模式启动，按各延迟项设定的秒数逐一等待后启动目标程序。

## 界面与图标

- 深色 Fluent 风格：深灰背景、蓝色强调色、圆角按钮、卡片化面板，所有样式集中在
  `src/StartupManager.Gui/Themes/Modern.xaml`，便于统一调整。
- 每行启动项**左侧显示该程序的真实图标**（通过 `SHGetFileInfo` P/Invoke 提取，
  无需 `System.Drawing.Common` 包；带内存缓存）。
- 「状态」列用绿色「已启用」/ 红色「已禁用」圆角徽标，一眼可辨。
- 添加 / 删除 / 延迟管理等操作均有清晰的状态栏反馈与管理员权限提示。

> 调试技巧：可用 `StartupManager.Gui.exe --render-preview <out.png>` 把主窗口
> 离屏渲染成 PNG（不需要桌面会话），用于验证样式与图标。注意在无桌面会话的沙箱中，
> 该 PNG 可能只渲染出背景色（虚拟化的数据行在无活动 PresentationSource 时不会被生成），
> 真实桌面环境下方可看到完整表格内容。

## 范围说明（本期未含）

计划任务、系统服务、驱动级自启动属于「完整档」功能，本期未实现；如需，可在
`StartupService` 中新增对应的 `IStartupProvider`（计划任务需引入 COM `TaskScheduler`，
服务需 `ServiceController`，均建议联网还原相关包后扩展）。

## 权限提示

- 修改 `HKLM` 与「所有用户启动文件夹」需要**管理员身份**运行。
- 修改被拒绝时，程序会给出明确的「没有权限…请以管理员身份运行」提示，不会崩溃。

## MCP 服务端（LLM 接入）

`src/StartupManager.Mcp` 把本工具的核心能力暴露为标准 **MCP (Model Context Protocol)**
服务端，采用 JSON-RPC 2.0 over **stdio**，零第三方依赖（不引入官方 SDK 也能离线构建），
任何支持 MCP 的 LLM 客户端都可接入并直接操作启动项与执行队列。

### 运行

```powershell
StartupManager.Mcp.exe                 # 使用默认 %LocalAppData% 队列清单
StartupManager.Mcp.exe --store <path>  # 指定队列清单路径（便于便携/测试）
StartupManager.Mcp.exe --version       # 打印版本
```

stdout 只输出协议帧（每行一个 JSON 对象），诊断信息走 stderr。

### 协议能力

- `initialize` / `notifications/initialized` / `ping`
- `tools/list` / `tools/call`

### 工具清单（18 个）

| 工具 | 作用 |
| --- | --- |
| `list_startup_entries` | 列出全部启动项 |
| `get_startup_entry` | 查询单个启动项（按 id 或 name+location） |
| `add_startup_entry` | 新增启动项（指定 location） |
| `remove_startup_entry` | 删除启动项 |
| `set_startup_enabled` | 启用 / 禁用启动项 |
| `set_startup_delay` | 设置延迟启动秒数 |
| `startup_stats` | 统计信息（总数/启用/禁用/延迟/运行时长） |
| `delayed_status` | 延迟器自启状态 + 延迟项 |
| `set_delayed_self_starter` | 开关「随登录自动启动延迟器」 |
| `launch_delayed_now` | 立即启动全部延迟项（测试） |
| `list_queues` | 列出执行队列 |
| `add_queue` | 新建队列 |
| `rename_queue` | 重命名队列 |
| `remove_queue` | 删除队列 |
| `add_queue_item` | 向队列添加应用（按 exe 去重） |
| `update_queue_item` | 修改队列项的启用/延迟 |
| `remove_queue_item` | 从队列删除应用 |
| `start_queue` | 一键启动队列（后台按延迟依次启动） |

### 客户端配置示例

```json
{
  "mcpServers": {
    "startup-manager": {
      "command": "D:\\path\\to\\StartupManager.Mcp.exe",
      "args": []
    }
  }
}
```

> 与 GUI 一样，修改 `HKLM` / 公共启动文件夹需要管理员权限；无权限时工具调用会返回
> `isError: true` 并附带中文原因，而不是让服务端崩溃。

