# 打包与部署

一键把程序打包到 `D:\StartupManager`，产物自带 .NET 运行时，目标机器无需安装任何依赖。

## 一键打包

在仓库根目录（`D:\Mygit\StartupManager`）执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

完成后 `D:\StartupManager` 中即为可运行的程序，直接双击 `StartupManager.Gui.exe` 启动。

> 如果双击脚本被策略拦截，用上面这条带 `-ExecutionPolicy Bypass` 的命令即可；
> 脚本已保存为 **UTF-8 with BOM**，Windows PowerShell 5.1 能正确读取中文。

## 脚本做了什么

| 步骤 | 说明 |
| --- | --- |
| 环境检查 | 确认 `dotnet` SDK、项目文件存在；自动关闭正在运行的旧实例，避免文件占用 |
| 编译 | `dotnet build` 整个解决方案（Release），有任何错误立即停止 |
| 发布 | 自包含发布 GUI 到 `D:\StartupManager`（`win-x64`） |
| 合并 MCP | 单独发布 MCP 服务端并拷入同一目录 |
| 冒烟验证 | 启动程序 5 秒，确认不会崩溃 |
| 清理 | 删除 `.pdb` 调试符号 |

## 注册为开机启动

```powershell
# 注册（写入 HKCU\...\Run，无需管理员）
powershell -ExecutionPolicy Bypass -File .\register-startup.ps1

# 撤销
powershell -ExecutionPolicy Bypass -File .\register-startup.ps1 -Remove
```

脚本会：

1. 写入启动项 `StartupManager` → 开机自动运行主程序
2. **校正延迟启动器** `StartupManagerDelayed` 的路径 —— 如果它仍指向旧的
   `bin\Debug\...` 目录（删除 Debug 输出后会导致延迟启动失效），会自动改为当前发布路径
3. 写入后立即回读校验，路径不存在则报错而不是静默成功

注册后可在 **任务管理器 → 启动应用** 中查看或禁用。

> 说明：`StartupManager`（开机启动本程序）与 `StartupManagerDelayed`（延迟启动器，
> 带 `--delayed-launch` 无窗口参数）是两个独立项，可共存。后者由程序
> 「延迟启动」页的开关维护。

## 参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `-OutputDir` | `D:\StartupManager` | 发布目标目录 |
| `-SingleFile` | 关 | 发布为单文件 exe。默认关闭，多文件对 MCP 子进程更稳 |
| `-NoClean` | 关 | 保留目标目录已有文件 |
| `-SkipTests` | 关 | 跳过启动冒烟验证 |

示例：

```powershell
# 发布到其它位置
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -OutputDir 'E:\Tools\StartupManager'

# 单文件（体积更小，但 MCP 需要额外文件，不推荐）
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SingleFile
```

## 产物说明

| 文件 | 作用 |
| --- | --- |
| `StartupManager.Gui.exe` | 主程序（图形界面），带自定义图标 |
| `StartupManager.Mcp.exe` | MCP 服务端，供 LLM 客户端接入；必须与主程序同目录 |
| `StartupManager.Core.dll` | 核心逻辑 |
| 其余 `.dll` | 自带 .NET 运行时，无需另装 |

体积约 140 MB，主要是自包含运行时。

## 接入 LLM（MCP）

主程序里打开 **设置 → MCP 服务…**，可查看状态并一键复制客户端配置。典型配置：

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

## 关于「未知的发行商」

Windows 显示「未知的发行商」是因为 exe **没有数字签名**，不是程序有 bug。

程序里能做和已经做好的部分：

- 已补齐版本信息（资源管理器 → 属性 → 详细信息）：产品名、描述、公司、版本、版权
- 已声明 `asInvoker` 清单，**不会**触发 UAC 提权弹窗（不需要管理员权限即可运行）

要让发布者显示为真实名称，必须**用代码签名证书签名**：

| 方式 | 效果 | 成本 |
| --- | --- | --- |
| 不签名（当前） | 显示「未知的发行商」 | 免费 |
| 自签名证书 | **仅导入证书的本机**认可；发给别人仍是未知 | 免费 |
| 公共 CA 证书（DigiCert / Sectigo 等） | 所有 Windows 机器都认可 | 需购买 |

### 签名操作

```powershell
# 自签名（仅本机有效）
powershell -ExecutionPolicy Bypass -File .\sign.ps1 -SelfSigned -TrustCert

# 使用正式证书（推荐用于分发）
powershell -ExecutionPolicy Bypass -File .\sign.ps1 `
    -PfxPath 'D:\cert\mycert.pfx' -PfxPassword 'your-password' `
    -TimestampServer 'http://timestamp.digicert.com'
```

> 脚本默认**不**连时间戳服务器：离线环境连它会长时间卡住。
> 正式分发时建议加上 `-TimestampServer`，否则证书过期后签名会失效。
> `-TrustCert` 会把自签名证书加入「当前用户 → 受信任的根」，仅本机生效。

### 补充说明

- **SmartScreen 提示**（“Windows 已保护你的电脑”）同样只能靠可信签名消除；
  未签名程序即使点了「仍要运行」，下载的副本仍会被拦。
- 从网络下载的 exe 会带「网络来源标记」，可在文件属性里勾选「解除锁定」，
  或用 `Unblock-File .\StartupManager.Gui.exe` 清除。

## 队列数据存放位置

执行队列保存在：

```
%LOCALAPPDATA%\StartupManager\Queues\queues.json
```

写入策略：

| 模式 | 行为 | 适用 |
| --- | --- | --- |
| **默认** | 在 `%TEMP%` 建临时文件 → 原子 Move 覆盖 | 正常环境，崩溃也不会损坏原文件 |
| **`--direct-write`** | 直接覆盖 `queues.json`，不建临时文件 | 环境禁止新建文件、但允许覆盖已有文件时 |

默认模式下若原子替换连续失败，会自动降级为就地覆盖，保证数据不丢。

### 写入失败时的诊断

失败信息会带上**异常类型与 Win32 错误码**，用于区分「权限不足」和「文件被占用」：

```
队列已修改但写入磁盘失败：直接写入失败：UnauthorizedAccessException Win32=5 — 拒绝访问（权限不足或被安全策略/沙箱拦截，不是文件被占用）
Win32 错误码：5（这是「权限不足」，不是文件被占用。）
```

| 码 | 含义 |
| --- | --- |
| 5 | 拒绝访问（权限） |
| 32 / 33 | 文件被其它进程占用 / 锁定 |
| 183 | 目标已存在，或路径中某段是文件而非目录 |

### 无法写入时怎么办

若确认是权限受限（而不是占用），有两个选择：

```powershell
# 1. 换存储位置
StartupManager.Mcp.exe --store "D:\StartupManager\data\queues.json"

# 2. 直接覆盖写，不建临时文件
StartupManager.Mcp.exe --direct-write
```

> 注意：`--store` 指定的文件必须与 GUI 使用的是**同一个**，否则两边看到不同队列。
> GUI 目前固定使用 `%LOCALAPPDATA%`，因此正常桌面环境下**不要改路径**，保持默认即可。

> 注意：`--store` 指定的文件必须与 GUI 使用的**是同一个**，否则两边看到不同队列。
> 若要让 GUI 也用该路径，需要把同一路径传给 GUI（GUI 目前固定使用 %LOCALAPPDATA%）。
> 因此在正常桌面环境下，建议不要改路径，保持默认即可。

## 注意事项

- 本程序修改 `HKCU` 与用户启动文件夹，**不需要管理员权限**。
- 修改 `HKLM`（本机 Run）或公共启动文件夹时，才需要以管理员身份运行。
- 若添加启动项后提示“写入未生效/没有注册表访问权限”，说明当前进程被限制
  （例如在受限沙箱、服务或某些安全软件环境中运行）。请用**普通桌面方式**直接启动
  `StartupManager.Gui.exe` 再试。
- 同理，若队列保存提示 “Access ... is denied”，说明当前进程对该路径无写权限，
  请用普通桌面方式运行，或给 MCP 加 `--store` 指向可写位置。
