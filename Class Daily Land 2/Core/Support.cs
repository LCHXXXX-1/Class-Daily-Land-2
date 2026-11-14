using ClassDailyLand2.Core.Storage;
using System.Text;

namespace ClassDailyLand2.Core;

/// <summary>
/// 本地日志（app.log，追加写）。业务数据仍走 AtomicJson。
///
/// 三个出口：
///   1. 磁盘 app.log（含 TRACE，超过 512KB 自动截断到尾部 64KB）
///   2. <see cref="RecentLines"/> 内存环形缓冲（日志页初始内容，容量 3000 行）
///   3. <see cref="LineAppended"/> 实时订阅（日志页逐行追加；可能来自任意线程，UI 自己切线程）
///
/// Core 层（AtomicJson / ScheduleEngine / Managers）拿不到 Logger 实例，
/// 通过 <see cref="Current"/> 静态引用写详细日志；未启动装配时 Current 为 null，全部静默。
/// </summary>
public sealed class Logger
{
    static readonly object Sync = new();

    /// <summary>只保护磁盘 app.log 的写入/截断/删除（与内存缓冲的 <see cref="Sync"/> 分开，见 <see cref="Write"/>）。</summary>
    readonly object _ioLock = new();
    readonly string _path;

    /// <summary>当前进程的活动 Logger（AppController.Startup 时赋值）。</summary>
    public static Logger? Current { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>新增一行（含级别与时间戳的完整文本）。任意线程触发。</summary>
    public event Action<string>? LineAppended;

    const int RecentCapacity = 3000;
    readonly Queue<string> _recent = new(RecentCapacity);
    int _capacity = DefaultCapacity;
    int _writesSinceRotate = 200;

    /// <summary>内存环形缓冲的默认上限（设置项 log_max_lines 的缺省值）。</summary>
    public const int DefaultCapacity = 2000;

    /// <summary>上限允许的最小值。</summary>
    public const int MinCapacity = 500;

    /// <summary>上限允许的最大值。</summary>
    public const int MaxCapacity = 3000;

    /// <summary>
    /// 内存里最多保留多少行。超出的旧行直接丢弃（磁盘 app.log 另有自己的截断策略）。
    /// 调小时会立刻裁掉多余的旧行。
    /// </summary>
    public int Capacity
    {
        get { lock (Sync) return _capacity; }
        set
        {
            var clamped = Math.Clamp(value, MinCapacity, MaxCapacity);
            lock (Sync)
            {
                _capacity = clamped;
                while (_recent.Count > _capacity) _recent.Dequeue();
            }
        }
    }

    /// <summary>最近的日志行（新行在尾部）。读取时返回快照，线程安全。</summary>
    public IReadOnlyList<string> RecentLines
    {
        get { lock (Sync) return _recent.ToArray(); }
    }

    /// <summary>清空内存里的最近日志（不动磁盘 app.log）。</summary>
    public void ClearRecent()
    {
        lock (Sync) _recent.Clear();
    }

    /// <summary>
    /// 清掉磁盘上的全部日志：<c>app.log</c> 与同目录下所有 <c>error-*.log</c> 诊断快照，
    /// 内存缓冲一并清空。返回 (删除文件数, 释放字节数)。
    ///
    /// 删完会补一条记录 —— 否则用户回头看到"日志突然是空的"会以为是程序坏了。
    /// </summary>
    public (int Files, long Bytes) ClearAll()
    {
        var dir = Path.GetDirectoryName(_path);
        var files = 0;
        long bytes = 0;

        lock (Sync)
        {
            _recent.Clear();
        }

        // 文件操作走 _ioLock：别在别人写到一半时把 app.log 删了
        lock (_ioLock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    bytes += new FileInfo(_path).Length;
                    File.Delete(_path);
                    files++;
                }
                catch { /* 删不掉就留着，别把清除操作变成崩溃 */ }
            }

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "error-*.log"))
                    {
                        try
                        {
                            bytes += new FileInfo(file).Length;
                            File.Delete(file);
                            files++;
                        }
                        catch { /* 单个失败不影响其余 */ }
                    }
                }
                catch { /* 目录不可读就跳过 */ }
            }
        }

        Write("INFO", $"[日志] 已清除 {files} 个日志文件，释放 {bytes / 1024.0:0.#} KB（含自动保存的报错快照）");
        return (files, bytes);
    }

    public Logger(DataPaths paths)
    {
        _path = paths.Log;
        DataRoot = paths.Root;
    }

    /// <summary>真实版本号（VersionInfo.Version）；未设置时退回程序集版本。</summary>
    public string? VersionOverride { get; set; }

    /// <summary>日志里显示用的版本号。</summary>
    public string DisplayVersion => string.IsNullOrWhiteSpace(VersionOverride) ? AppVersion : VersionOverride!;

    /// <summary>数据根目录（运行标记、设置都在这里）。报信进程清运行标记时要用。</summary>
    public string DataRoot { get; }

    /// <summary>
    /// 把最近 <paramref name="maxLines"/> 行日志导出成一个独立的诊断文件，返回路径（失败返回 null）。
    /// 出错时自动调一次，错误窗口上的「导出日志」也走它 —— 用户发文件比发截图有用得多。
    /// </summary>
    public string? SaveSnapshot(string kind, int maxLines = 200, string? note = null)
    {
        try
        {
            var lines = RecentLines;
            if (maxLines > 0 && lines.Count > maxLines)
                lines = lines.Skip(lines.Count - maxLines).ToArray();

            var dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return null;
            Directory.CreateDirectory(dir);

            var file = Path.Combine(dir, $"error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"Class Daily Land 2 · {kind}");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"版本：{DisplayVersion}");
            if (!string.IsNullOrWhiteSpace(note)) sb.AppendLine($"说明：{note}");
            sb.AppendLine($"以下为最近 {lines.Count} 条日志");
            sb.AppendLine(new string('-', 64));
            foreach (var line in lines) sb.AppendLine(line);

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            return file;
        }
        catch
        {
            return null;   // 存诊断失败不能反过来把程序弄崩
        }
    }

    /// <summary>诊断文件抬头用的版本号（拿不到就写 unknown）。</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var v = typeof(Logger).Assembly.GetName().Version;
                return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>日志目录（app.log 与 error-*.log 都在这里）。给报告落盘与"清除日志"用。</summary>
    public string LogDirectory => Path.GetDirectoryName(_path) ?? "";

    /// <summary>最详细一级：每次文件读写、每次计算都走这里。</summary>
    public void Trace(string message) => Write("TRACE", message);
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    // ————————————————————— 结构化埋点（排障时按前缀搜） —————————————————————

    /// <summary>分区横幅：把一次会话切成"启动 / 更新 / 退出"等大段，方便肉眼定位。</summary>
    public void Section(string title) => Write("INFO", $"---------- {title} ----------");

    /// <summary>流程步骤（启动流程、更新流程等），可带耗时。</summary>
    public void Step(string area, string step, long elapsedMs = -1) =>
        Write("INFO", elapsedMs >= 0 ? $"[{area}] {step}（{elapsedMs}ms）" : $"[{area}] {step}");

    /// <summary>状态流转：谁变成谁（窗口显隐、更新阶段、插件状态）。</summary>
    public void Flow(string area, string description) => Write("INFO", $"[{area}] {description}");

    /// <summary>用户动作：点了哪个菜单项、切了哪个开关（做"用户到底做了啥"的现场还原）。</summary>
    public void Action(string area, string description) => Write("INFO", $"[{area}] 用户操作：{description}");

    /// <summary>HTTP 请求与响应：方法、地址、状态码、耗时、字节数。</summary>
    public void Http(string method, string url, int? status, long elapsedMs, long? bytes = null, string? note = null)
    {
        var level = status is >= 400 ? "WARN" : "INFO";
        var size = bytes is > 0 ? $", {bytes}B" : "";
        var state = status is null ? "无响应" : status.ToString();
        var extra = string.IsNullOrWhiteSpace(note) ? "" : $" · {note}";
        Write(level, $"[HTTP] {method} {url} → {state}（{elapsedMs}ms{size}）{extra}");
    }

    /// <summary>文件操作：动作、路径、成败、附注。</summary>
    public void FileOp(string action, string path, bool ok, string? note = null)
    {
        var extra = string.IsNullOrWhiteSpace(note) ? "" : $" · {note}";
        Write(ok ? "TRACE" : "WARN", $"[文件] {action} {(ok ? "成功" : "失败")}：{path}{extra}");
    }

    /// <summary>耗时采样（渲染、计算、IO），TRACE 级，别在热循环里调。</summary>
    public void Perf(string name, long elapsedMs) => Write("TRACE", $"[耗时] {name}：{elapsedMs}ms");

    /// <summary>带错误码的异常记录：一条日志里同时有码、原因和堆栈。</summary>
    public void Error(string code, string message, Exception? exception = null)
    {
        var detail = exception is null ? "" : $"{Environment.NewLine}{exception}";
        Write("ERROR", $"[{code}] {message}{detail}");
    }

    /// <summary>内存与线程快照（启动/退出/疑似泄漏时看）。</summary>
    public void Memory(string tag)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            Write("INFO", $"[内存] {tag}：工作集 {proc.WorkingSet64 / 1048576.0:0.0} MB，" +
                           $"私有 {proc.PrivateMemorySize64 / 1048576.0:0.0} MB，" +
                           $"GC 堆 {GC.GetTotalMemory(false) / 1048576.0:0.0} MB，" +
                           $"线程 {proc.Threads.Count}，句柄 {proc.HandleCount}");
        }
        catch (Exception ex)
        {
            Write("WARN", "[内存] 采样失败：" + ex.Message);
        }
    }

    /// <summary>环境横幅：一条日志把"什么机器、什么版本、什么权限"钉死。</summary>
    public void EnvironmentBanner()
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var elevated = false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { /* 查不到就当非提权 */ }

            Write("INFO", $"[环境] 版本 {DisplayVersion} · 进程 {proc.Id} · 线程 {proc.Threads.Count}");
            Write("INFO", $"[环境] 系统 {Environment.OSVersion} · {(Environment.Is64BitProcess ? "x64" : "x86")} · " +
                           $".NET {Environment.Version} · 处理器 {Environment.ProcessorCount} 核");
            Write("INFO", $"[环境] 用户 {Environment.UserName} · 提权 {elevated} · 中文界面 {System.Globalization.CultureInfo.CurrentUICulture.Name}");
            Write("INFO", $"[环境] 程序目录 {AppContext.BaseDirectory}");
            Write("INFO", $"[环境] 数据目录 {DataRoot}（存在 {Directory.Exists(DataRoot)}）");
            Write("INFO", $"[环境] 工作集 {proc.WorkingSet64 / 1048576.0:0.0} MB · 启动于 {proc.StartTime:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Write("WARN", "[环境] 横幅采集失败：" + ex.Message);
        }
    }

    void Write(string level, string message)
    {
        if (!Enabled) return;
        string line;
        lock (Sync)
        {
            line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            _recent.Enqueue(line);
            while (_recent.Count > _capacity) _recent.Dequeue();
        }

        // ★ 磁盘写挪到独立的 _ioLock 里：原来 AppendAllText 和整读整写的截断检查
        //   都压在全局 Sync 内，UI 线程每秒打几条 TRACE 时，日志页读 RecentLines、
        //   ClearAll、别的线程记日志全都要陪着磁盘 IO 一起等。
        //   行序仍由 _ioLock 保证（内存入队顺序与落盘顺序可能差一位，日志可接受）。
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);

                // 截断检查每 200 行做一次：TRACE 全开时每秒好几行，
                // 逐行 stat + 读整个 512KB 文件会把写日志变成性能热点。
                if (++_writesSinceRotate >= 200)
                {
                    _writesSinceRotate = 0;
                    var info = new FileInfo(_path);
                    if (info.Length > 512 * 1024)   // 超过 512KB 截断到尾部 64KB
                        TruncateToTail(64 * 1024);
                }
            }
            catch { /* 日志失败不能影响业务 */ }
        }

        try { LineAppended?.Invoke(line); }
        catch { /* 订阅方异常不能影响业务 */ }
    }

    /// <summary>
    /// 把 app.log 截到尾部 <paramref name="keepBytes"/> 字节。
    /// 只读尾部（原来 ReadAllText 会把整个 512KB 读进内存再整写），且保留 UTF-8 BOM：
    /// 原来用无 BOM 的 WriteAllText，截断后文件头就没了 BOM，和追加写的编码约定不一致。
    /// </summary>
    void TruncateToTail(int keepBytes)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length <= keepBytes) return;

        var start = fs.Length - keepBytes;
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        fs.ReadExactly(buf);

        if (start > 0)
        {
            // 别把某个汉字/多字节字符从中间切开：跳到第一个换行之后
            var nl = Array.IndexOf(buf, (byte)'\n');
            if (nl >= 0 && nl + 1 < buf.Length) buf = buf[(nl + 1)..];
        }

        // 直接按字节拼 BOM + 尾部，不做 解码→再编码（会把切坏的序列替换成 ?）
        var preamble = Encoding.UTF8.GetPreamble();
        var output = new byte[preamble.Length + buf.Length];
        preamble.CopyTo(output, 0);
        buf.CopyTo(output, preamble.Length);

        fs.Seek(0, SeekOrigin.Begin);
        fs.Write(output);
        fs.SetLength(output.Length);
        fs.Flush(true);
    }
}

/// <summary>缓动函数（UI 动画与自绘共用）。</summary>
public static class Easing
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double OutQuint(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 5);
}
