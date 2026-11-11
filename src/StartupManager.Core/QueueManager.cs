using System.Text;
using System.Text.Json;
using System.Threading;
using StartupManager.Core.Models;

namespace StartupManager.Core;

/// <summary>
/// Manages user-defined "execution queues" — named groups of applications that can
/// be launched together with one click. Configuration is persisted as JSON under
/// LocalApplicationData so it survives restarts (no admin required).
/// </summary>
public class QueueManager
{
    private static readonly string DefaultStoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartupManager", "Queues");
    private static readonly string DefaultStorePath = Path.Combine(DefaultStoreDir, "queues.json");

    private readonly List<StartupQueue> _queues = new();
    private readonly string _storePath;

    /// <summary>
    /// When true, <see cref="Save"/> writes straight to the store file without staging a
    /// temp file and moving it. Useful when creating new files is blocked but overwriting
    /// the existing store is allowed. Trades atomicity for reachability.
    /// </summary>
    public bool DirectWrite { get; set; }

    /// <summary>Create a manager using the default %LocalAppData% store.</summary>
    public QueueManager() : this(DefaultStorePath) { }

    /// <summary>Create a manager persisting to a specific file (useful for tests / portable use).</summary>
    /// <param name="storePath">File to persist to.</param>
    /// <param name="directWrite">Skip temp-file staging and overwrite the store in place.</param>
    public QueueManager(string storePath, bool directWrite = false)
    {
        _storePath = storePath;
        DirectWrite = directWrite;
    }

    public IReadOnlyList<StartupQueue> Queues => _queues;

    /// <summary>The file this manager persists to.</summary>
    public string StorePath => _storePath;

    public static string StoreFilePath => DefaultStorePath;

    public void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            // Share.Write|Delete so a concurrent atomic replace (another process) is not blocked,
            // and we are not blocked by it either.
            using var fs = new FileStream(_storePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var data = JsonSerializer.Deserialize<List<StartupQueue>>(json);
            _queues.Clear();
            if (data is not null) _queues.AddRange(data);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QueueManager] load failed: {ex}");
        }
    }

    /// <summary>Last persistence error (null when the most recent save succeeded).</summary>
    public string? LastError { get; private set; }

    /// <summary>Detailed, machine-readable description of the last failure (type + Win32 code + meaning).</summary>
    public string? LastErrorDetail { get; private set; }

    /// <summary>Win32 error code of the last failure, when one was reported (e.g. 5=denied, 32=in use).</summary>
    public int? LastWin32Error { get; private set; }

    private const int SaveAttempts = 6;

    /// <summary>
    /// Persist to disk.
    ///
    /// Default path: stage a temp file in <see cref="Path.GetTempPath"/> and move it onto
    /// the store, so readers never see a half-written file and a crash cannot truncate
    /// the store.
    ///
    /// When <see cref="DirectWrite"/> is on (or the move keeps failing), the store is
    /// overwritten in place — non-atomic, but works where creating new files is denied.
    /// </summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(_queues, new JsonSerializerOptions { WriteIndented = true });

        var dir = Path.GetDirectoryName(_storePath);

        // ---- 0) direct write: skip temp staging entirely ----
        if (DirectWrite)
        {
            WriteInPlace(json, dir, "直接写入");
            return;
        }

        Exception? last = null;

        // ---- 1) write into %TEMP%, then move onto the store ----
        for (var attempt = 0; attempt < SaveAttempts; attempt++)
        {
            var tmp = Path.Combine(
                Path.GetTempPath(),
                $"startupmanager-queues-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Temp write and store replacement are reported separately so the log
                // makes it obvious *which* step was denied.
                try
                {
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    LogFailure("写入临时文件", ex, tmp);
                    throw;
                }

                try
                {
                    File.Move(tmp, _storePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    LogFailure("替换队列文件", ex, _storePath);
                    throw;
                }

                LastError = null;
                LastErrorDetail = null;
                LastWin32Error = null;
                CleanupStaleTempFiles();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                TryDelete(tmp);
                if (attempt < SaveAttempts - 1)
                    Thread.Sleep(40 * (attempt + 1)); // 40,80,120,160,200 ms backoff
            }
            catch (Exception ex)
            {
                // Not transient (e.g. serialization failure) — retrying won't help.
                RecordFailure("序列化/未知错误", ex, _storePath);
                TryDelete(tmp);
                return;
            }
        }

        // ---- 2) fallback: overwrite the existing store in place ----
        if (WriteInPlace(json, dir, "就地覆盖", logSuccess: true))
            return;

        LastError ??= last?.Message;
        Console.Error.WriteLine(
            $"[QueueManager] save failed after {SaveAttempts} attempts. " +
            $"detail={LastErrorDetail ?? last?.ToString()}");
    }

    /// <summary>
    /// Overwrite the store file in place (no temp file). Returns true on success and
    /// records the failure with full diagnostics otherwise.
    /// </summary>
    private bool WriteInPlace(string json, string? dir, string step, bool logSuccess = false)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(_storePath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite);
            fs.SetLength(0);
            var bytes = new UTF8Encoding(false).GetBytes(json);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);

            LastError = null;
            LastErrorDetail = null;
            LastWin32Error = null;
            if (logSuccess)
                Console.Error.WriteLine("[QueueManager] temp+move unavailable; wrote in place instead.");
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(step, ex, _storePath);
            return false;
        }
    }

    /// <summary>
    /// Turn an exception into "type + Win32 code + human meaning" so a permission problem
    /// (5) is never confused with a sharing violation (32/33).
    /// </summary>
    private static (string Detail, int? Win32) DescribeException(Exception ex)
    {
        var win32 = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode
                    ?? ex.HResult & 0xFFFF;

        // HResult for Win32-mapped errors is 0x80070000 | code; recover that code.
        if (win32 is 0 or 0x8007 && ex.HResult != 0)
            win32 = ex.HResult & 0xFFFF;

        if (win32 is <= 0 or > 0xFFFF) win32 = 0;

        string meaning;
        if (win32 > 0)
        {
            meaning = win32 switch
            {
                5 => "拒绝访问（权限不足或被安全策略/沙箱拦截，不是文件被占用）",
                32 => "文件正被另一个进程使用（共享冲突）",
                33 => "文件被锁定（部分锁区域冲突）",
                2 => "找不到文件",
                3 => "找不到路径的一部分（父目录不存在）",
                80 => "目标已存在",
                183 => "目标已存在，或路径中有一段是文件而非目录",
                112 => "磁盘空间不足",
                1392 => "文件或目录已损坏且无法读取",
                19 => "磁盘写保护",
                _ => new System.ComponentModel.Win32Exception(win32).Message,
            };
        }
        else
        {
            meaning = ex.Message;
        }

        var codeText = win32 > 0 ? $"Win32={win32}" : "Win32=(n/a)";
        return ($"{ex.GetType().Name} {codeText} — {meaning}", win32 > 0 ? win32 : null);
    }

    private void LogFailure(string step, Exception ex, string path)
    {
        var (detail, win32) = DescribeException(ex);
        Console.Error.WriteLine($"[QueueManager] {step}失败 @ {path}\n  {detail}");
    }

    private void RecordFailure(string step, Exception ex, string path)
    {
        var (detail, win32) = DescribeException(ex);
        LastError = $"{step}失败：{detail}";
        LastErrorDetail = detail;
        LastWin32Error = win32;
        Console.Error.WriteLine($"[QueueManager] {step}失败 @ {path}\n  {detail}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Remove leftovers from a crashed writer. Covers both the current temp location
    /// (%TEMP%) and the store directory used by older versions, so upgrading does not
    /// leave orphaned files behind.
    /// </summary>
    private void CleanupStaleTempFiles()
    {
        var stale = TimeSpan.FromMinutes(5);

        void Sweep(string dir, string pattern)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > stale) File.Delete(f);
                    }
                    catch { /* another process may own it */ }
                }
            }
            catch { /* best effort */ }
        }

        // Current location: %TEMP%\startupmanager-queues-*.tmp
        Sweep(Path.GetTempPath(), "startupmanager-queues-*.tmp");

        // Legacy location: <store dir>\queues.json.<pid>.<hash>.tmp
        var storeDir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(storeDir))
            Sweep(storeDir, Path.GetFileName(_storePath) + ".*.tmp");
    }

    public StartupQueue AddQueue(string name)
    {
        var q = new StartupQueue { Name = name };
        _queues.Add(q);
        Save();
        return q;
    }

    public void RemoveQueue(string id)
    {
        _queues.RemoveAll(q => q.Id == id);
        Save();
    }

    public void RenameQueue(string id, string name)
    {
        var q = _queues.FirstOrDefault(x => x.Id == id);
        if (q is not null) { q.Name = name; Save(); }
    }

    public void AddItem(string queueId, string name, string command, bool enabled, int delaySeconds)
    {
        var q = _queues.FirstOrDefault(x => x.Id == queueId);
        if (q is null) return;
        // de-duplicate by executable path so the same .exe can't appear twice
        var exe = NormalizeExe(command);
        if (exe is not null && q.Items.Any(i => NormalizeExe(i.Command) == exe))
            return;
        q.Items.Add(new QueueItem
        {
            Name = name,
            Command = command,
            Enabled = enabled,
            DelaySeconds = delaySeconds,
            Order = q.Items.Count,
        });
        Save();
    }

    private static string? NormalizeExe(string command)
        => StartupEntry.SplitCommandLine(command).Exe?.Trim().Trim('"').ToLowerInvariant();

    public void RemoveItem(string queueId, string itemName)
    {
        var q = _queues.FirstOrDefault(x => x.Id == queueId);
        if (q is null) return;
        for (var i = q.Items.Count - 1; i >= 0; i--)
            if (q.Items[i].Name == itemName) q.Items.RemoveAt(i);
        Save();
    }

    public void UpdateItem(string queueId, QueueItem item)
    {
        var q = _queues.FirstOrDefault(x => x.Id == queueId);
        var existing = q?.Items.FirstOrDefault(i => i.Name == item.Name);
        if (existing is not null)
        {
            existing.Command = item.Command;
            existing.Enabled = item.Enabled;
            existing.DelaySeconds = item.DelaySeconds;
        }
        Save();
    }

    /// <summary>
    /// Replace an item's editable fields, looked up by its current name so a rename
    /// works too. Rejects a rename that would collide with another item's name.
    /// </summary>
    /// <returns>Null on success, otherwise a human-readable reason.</returns>
    public string? EditItem(
        string queueId,
        string currentName,
        string newName,
        string command,
        int delaySeconds,
        bool enabled)
    {
        var q = _queues.FirstOrDefault(x => x.Id == queueId);
        if (q is null) return "找不到该队列。";

        var item = q.Items.FirstOrDefault(i => i.Name == currentName);
        if (item is null) return $"队列中找不到 “{currentName}”。";

        if (string.IsNullOrWhiteSpace(newName)) return "名称不能为空。";
        if (string.IsNullOrWhiteSpace(command)) return "命令不能为空。";

        var renamed = !string.Equals(newName, currentName, StringComparison.Ordinal);
        if (renamed && q.Items.Any(i => !ReferenceEquals(i, item) &&
                                        string.Equals(i.Name, newName, StringComparison.Ordinal)))
            return $"已存在名为 “{newName}” 的项，请换一个名称。";

        item.Name = newName;
        item.Command = command;
        item.DelaySeconds = delaySeconds < 0 ? 0 : delaySeconds;
        item.Enabled = enabled;

        Save();
        return null;
    }

    /// <summary>
    /// Move an item to a new position and renumber every item's Order so the new
    /// sequence is what StartQueue launches and what survives a restart.
    /// </summary>
    /// <returns>True when something actually moved.</returns>
    public bool MoveItem(string queueId, int fromIndex, int toIndex)
    {
        var q = _queues.FirstOrDefault(x => x.Id == queueId);
        if (q is null) return false;

        var count = q.Items.Count;
        if (fromIndex < 0 || fromIndex >= count) return false;

        // Clamp instead of failing: dropping past the last row is a normal gesture.
        toIndex = Math.Clamp(toIndex, 0, count - 1);
        if (fromIndex == toIndex) return false;

        q.Items.Move(fromIndex, toIndex);

        for (var i = 0; i < q.Items.Count; i++)
            q.Items[i].Order = i;

        Save();
        return true;
    }

    /// <summary>One step of a queue run, reported to the UI so it can draw a timeline.</summary>
    public sealed class QueueRunStep
    {
        public string Name { get; init; } = "";
        public int DelaySeconds { get; init; }
        public int Index { get; init; }       // 0-based position among enabled items
        public int Total { get; init; }       // number of enabled items
        public double AtSeconds { get; init; } // scheduled time offset from queue start
    }

    public enum QueueRunState
    {
        Starting,   // about to wait for the next item
        Waiting,    // sleeping until this item's delay elapses
        Launching,  // process is being started
        Launched,   // started successfully
        Failed,     // could not start
        Finished,   // all items done
    }

    /// <summary>Progress notification for a running queue.</summary>
    public sealed class QueueRunProgress
    {
        public QueueRunState State { get; init; }
        public QueueRunStep? Step { get; init; }
        public string? Message { get; init; }

        /// <summary>
        /// The underlying failure, when <see cref="State"/> is <see cref="QueueRunState.Failed"/>.
        /// Kept as the real exception so callers can show type + Win32 code + message instead
        /// of a generic "启动失败" with no clue what went wrong.
        /// </summary>
        public Exception? Error { get; init; }

        /// <summary>
        /// Human-readable reason for the failure (e.g. "找不到文件", "系统找不到指定的文件",
        /// "拒绝访问"). Empty for a successful step. This is what the UI surfaces in the timeline.
        /// </summary>
        public string? ErrorDetail { get; init; }
    }

    /// <summary>
    /// Turn an exception into a compact, user-facing reason. Win32-mapped errors get a
    /// friendly hint (file not found / access denied / no association), so a queue failure
    /// is never reported as an opaque "启动失败".
    /// </summary>
    public static string DescribeLaunchError(Exception ex)
    {
        // Only trust the low 16 bits as a Win32 code when the HResult really is
        // FACILITY_WIN32 (0x80070000) — otherwise a generic COM/OOM error would be
        // reported as a meaningless "Win32=16389" and mislead the user.
        var hresult = unchecked((uint)ex.HResult);
        var win32 = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode
                    ?? ((hresult & 0xFFFF0000) == 0x80070000 ? (int)(hresult & 0xFFFF) : 0);
        if (win32 is <= 0 or > 0xFFFF) win32 = 0;

        var meaning = win32 switch
        {
            2 => "系统找不到指定的文件（可执行文件路径不存在）",
            3 => "系统找不到指定的路径",
            5 => "拒绝访问（权限不足或被安全软件拦截）",
            32 => "文件正被另一个进程占用",
            1155 => "没有关联的应用程序（文件类型未注册 Shell 打开方式）",
            193 => "不是有效的可执行程序",
            216 => "程序需要以管理员权限运行，当前进程未提权",
            _ => ex.Message,
        };

        var code = win32 > 0 ? $"（Win32={win32}）" : "";
        return $"{ex.GetType().Name}{code}：{meaning}";
    }

    /// <summary>Launch every enabled item of a queue, waiting its own delay from start (background-safe).</summary>
    public static void StartQueue(StartupQueue queue, IProgress<QueueRunProgress>? progress = null)
    {
        var enabled = queue.Items.Where(i => i.Enabled).OrderBy(i => i.Order).ToList();
        var total = enabled.Count;
        var start = DateTime.UtcNow;
        var failures = 0;

        progress?.Report(new QueueRunProgress
        {
            State = QueueRunState.Starting,
            Message = total == 0
                ? "该队列没有启用的应用。"
                : $"队列「{queue.Name}」开始执行，共 {total} 项。",
        });

        if (total == 0)
        {
            progress?.Report(new QueueRunProgress { State = QueueRunState.Finished, Message = "无启用的应用，已结束。" });
            return;
        }

        for (var idx = 0; idx < enabled.Count; idx++)
        {
            var it = enabled[idx];
            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
            var wait = (int)(it.DelaySeconds - elapsed);
            var step = new QueueRunStep
            {
                Name = it.Name,
                DelaySeconds = it.DelaySeconds,
                Index = idx,
                Total = total,
                AtSeconds = it.DelaySeconds,
            };

            if (wait > 0)
            {
                progress?.Report(new QueueRunProgress
                {
                    State = QueueRunState.Waiting,
                    Step = step,
                    Message = $"等待 {wait} 秒后启动「{it.Name}」…",
                });
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }

            progress?.Report(new QueueRunProgress
            {
                State = QueueRunState.Launching,
                Step = step,
                Message = $"正在启动第 {idx + 1}/{total} 项：{it.Name}",
            });

            Exception? error = null;
            try
            {
                var (exe, args) = StartupEntry.SplitCommandLine(it.Command);
                if (exe is null)
                {
                    // SplitCommandLine returns null only for empty/whitespace commands.
                    error = new InvalidOperationException("命令为空或无法解析出可执行程序。");
                }
                else
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args ?? "",
                        UseShellExecute = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
                    };
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                // Keep the real reason (missing file, denied, no shell association, …)
                // instead of swallowing it — the whole point is to *show* the error.
                error = ex;
            }

            var detail = error is null ? null : DescribeLaunchError(error);
            if (error is not null)
            {
                failures++;
                // Also mirror to stderr so headless callers (MCP / CLI / logs) see the cause.
                Console.Error.WriteLine(
                    $"[QueueManager] 启动失败：「{it.Name}」 命令={it.Command}\n  {detail}");
            }

            progress?.Report(new QueueRunProgress
            {
                State = error is null ? QueueRunState.Launched : QueueRunState.Failed,
                Step = step,
                Error = error,
                ErrorDetail = detail,
                Message = error is null
                    ? $"已启动第 {idx + 1}/{total} 项：{it.Name}"
                    : $"启动失败第 {idx + 1}/{total} 项：{it.Name} — {detail}",
            });
        }

        progress?.Report(new QueueRunProgress
        {
            State = QueueRunState.Finished,
            Message = failures == 0
                ? $"队列「{queue.Name}」执行完毕，共 {total} 项，全部成功。"
                : $"队列「{queue.Name}」执行完毕，共 {total} 项，{failures} 项启动失败（见各步骤错误）。",
        });
    }
}
