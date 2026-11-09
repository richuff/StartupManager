# StartupManager

Windows 启动项与执行队列管理工具。原生 **C# / .NET 10 + WPF**，零第三方依赖，
同时提供 **图形界面 (GUI)**、**命令行 (CLI)** 与 **MCP 服务端**（供 LLM 接入）。

## 功能

### 启动项管理

- 列出本机全部自启动项（跨多个位置统一展示）
- 启用 / 禁用 / 删除已有启动项
- 新增启动项到指定位置
- 每行显示程序的真实图标，状态用彩色徽标区分
- 点击表头排序（再点一次切换升降序，带箭头指示）

### 延迟启动

统一管理「登录后等待 N 秒再启动」的程序，把开机负载错开：

- 秒数从登录时刻起算，**各项互不累加**
- 可开启「随登录自动启动延迟器」
- 支持「立即启动一次」用于测试

### 执行队列

把多个程序编成一组，一键按各自延迟依次启动：

- 多队列管理，配置持久化保存
- **拖动行即可调整执行顺序**，也可右键上移/下移
- 「执行过程」时间轴实时展示每一步状态（待执行 / 等待中 / 启动中 / 已启动 / 失败）
- 按 exe 自动去重，避免同一程序重复出现在队列里
- 右键「修改…」可编辑名称、命令、延迟、启用状态
- 监听磁盘文件变化，外部修改（如 MCP 写入）会自动刷新

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

- 注册表：`Microsoft.Win32.Registry`
- 启动文件夹快捷方式：COM `IShellLink` / `IPersistFile`（STA 线程）
- 图标提取：`SHGetFileInfo`（不依赖 `System.Drawing.Common`，带缓存）
- 进程身份诊断：`GetTokenInformation`（读取完整性级别 / UAC 提权类型）

## 构建

```powershell
dotnet build StartupManager.slnx -c Release
```

## 打包发布

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

默认输出到 `D:\StartupManager`，**自带 .NET 运行时**，目标机器无需安装任何依赖。

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `-OutputDir` | `D:\StartupManager` | 发布目标目录 |
| `-SingleFile` | 关 | 单文件模式（MCP 子进程场景不建议） |
| `-NoClean` | 关 | 保留目标目录已有文件 |
| `-SkipTests` | 关 | 跳过启动冒烟验证 |

> 脚本每次都会先 `dotnet clean` 再发布，避免增量构建留下旧二进制。

## 注册为开机启动

```powershell
# 注册（写入 HKCU\...\Run，无需管理员）
powershell -ExecutionPolicy Bypass -File .\register-startup.ps1

# 撤销
powershell -ExecutionPolicy Bypass -File .\register-startup.ps1 -Remove
```

脚本还会把延迟启动器（`--delayed-launch`）的路径校正为当前发布位置，
避免它指向已被删除的旧输出目录。

## MCP 服务端（LLM 接入）

`src/StartupManager.Mcp` 把核心能力暴露为标准 **MCP (Model Context Protocol)** 服务端，
采用 **JSON-RPC 2.0 over stdio**，零第三方依赖，任何支持 MCP 的客户端都可接入。

### 运行

```powershell
StartupManager.Mcp.exe                    # 使用默认 %LocalAppData% 队列清单
StartupManager.Mcp.exe --store <path>     # 指定队列清单路径
StartupManager.Mcp.exe --direct-write     # 直接覆盖写，不建临时文件
StartupManager.Mcp.exe --whoami           # 打印进程身份（用户/完整性级别）
StartupManager.Mcp.exe --version          # 打印版本
```

stdout 只输出协议帧（每行一个 JSON 对象），诊断信息走 stderr。

### 协议能力

**协议版本协商**：服务端支持多个 MCP 版本，按客户端请求回显：

| 客户端请求 | 响应 |
| --- | --- |
| `2024-11-05` | `2024-11-05` |
| `2025-03-26` | `2025-03-26` |
| `2025-06-18` | `2025-06-18` |
| `2025-11-25` | `2025-11-25` |
| 其它合法 `YYYY-MM-DD`（如未来 2026 版本） | 原样回显 |
| 无法识别 / 省略 | 回退到 `2025-06-18` / `2024-11-05` |

支持的方法：

- `initialize` / `notifications/initialized` / `ping`
- `tools/list` / `tools/call`
- `resources/list` / `resources/templates/list` / `prompts/list`（返回空列表，供新版客户端探测）

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
| `start_queue` | 一键启动队列，返回执行计划 |

### 客户端配置示例

```json
{
  "mcpServers": {
    "startup-manager": {
      "command": "D:\\StartupManager\\StartupManager.Mcp.exe",
      "args": []
    }
  }
}
```

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 执行队列 | `%LOCALAPPDATA%\StartupManager\Queues\queues.json` |
| 延迟启动清单 | `%LOCALAPPDATA%\StartupManager\Delayed\manifest.json` |

写盘策略：默认在 `%TEMP%` 建临时文件后原子替换，失败时降级为就地覆盖；
失败会带上 **异常类型与 Win32 错误码**（`5`=权限、`32/33`=被占用），不会静默丢数据。

## 界面

- 左侧图标导航：启动项 / 延迟启动项 / 执行队列
- 顶栏显示应用图标，菜单：编辑 / 设置（MCP 服务、打开数据目录）/ 关于
- 列表支持点击表头排序、右键操作、拖动调整队列顺序
- 添加、关于、MCP 等弹窗为 Electron 风格（无边框圆角 + 阴影）
- **点击窗口关闭按钮会最小化到系统托盘**，不会退出程序；双击托盘图标或右键
  「显示主窗口」可恢复。真正退出请用托盘菜单的「退出」或「编辑 → 退出程序」。
  （托盘不可用时关闭按钮会正常退出，避免留下无法恢复的后台进程。）

## 权限与签名

- 修改 `HKCU` 与用户启动文件夹**不需要管理员权限**；`HKLM` 与公共启动文件夹才需要。
- 未签名的 exe 会显示「未知的发行商」，需代码签名证书才能消除：

```powershell
# 自签名（仅本机有效）
powershell -ExecutionPolicy Bypass -File .\sign.ps1 -SelfSigned -TrustCert

# 正式证书（分发用）
powershell -ExecutionPolicy Bypass -File .\sign.ps1 -PfxPath <cert.pfx> -PfxPassword <pwd>
```

- 进程需运行在**中等完整性**级别才能写用户目录；若以低完整性（如某些沙箱）启动，
  写盘会返回 `Win32=5`。可用 `--whoami` 查看当前进程的完整性级别。

## 范围说明（本期未含）

计划任务、系统服务、驱动级自启动属于「完整档」功能，本期未实现；如需，可在
`StartupService` 中新增对应的 `IStartupProvider`（计划任务需引入 COM `TaskScheduler`，
服务需 `ServiceController`，均建议联网还原相关包后扩展）。

详见 [DEPLOY.md](DEPLOY.md)。
