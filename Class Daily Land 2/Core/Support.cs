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
            sb.AppendLine($"版本：{AppVersion}");
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

    void Write(string level, string message)
    {
        if (!Enabled) return;
        string line;
        lock (Sync)
        {
            line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            _recent.Enqueue(line);
            while (_recent.Count > _capacity) _recent.Dequeue();
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
                    {
                        var text = File.ReadAllText(_path);
                        var keep = Math.Min(text.Length, 64 * 1024);
                        File.WriteAllText(_path, text.Substring(text.Length - keep));
                    }
                }
            }
            catch { /* 日志失败不能影响业务 */ }
        }
        try { LineAppended?.Invoke(line); }
        catch { /* 订阅方异常不能影响业务 */ }
    }
}

/// <summary>缓动函数（UI 动画与自绘共用）。</summary>
public static class Easing
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double OutQuint(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 5);
}
