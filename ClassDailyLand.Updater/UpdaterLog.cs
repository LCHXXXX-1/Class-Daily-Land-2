using System.Text;

namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器日志：一份文件 + 一份内存（窗口上能看最近几条）。
///
/// 更新器是**唯一还能动手的进程**（主程序已经退出），它自己出问题时用户只能看日志，
/// 所以每一步都要写 —— 尤其是"替换失败"这类，日志是事后唯一能定位的线索。
/// </summary>
public static class UpdaterLog
{
    static readonly object Sync = new();
    static readonly Queue<string> Recent = new();
    static string? _path;

    /// <summary>最近若干条日志（给窗口显示）。</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Sync) return Recent.ToArray();
    }

    public static void SetPath(string? path)
    {
        lock (Sync)
        {
            _path = path;
            if (path is null) return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { /* 路径坏了也不影响后续 */ }
        }
        Info("日志落点：" + (path ?? "(内存)"));
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

        lock (Sync)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 200) Recent.Dequeue();

            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去不能让更新器崩掉
            }
        }
    }
}
