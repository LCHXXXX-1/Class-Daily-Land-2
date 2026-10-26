using ClassDailyLand2.Core.Storage;
using System.Text;

namespace ClassDailyLand2.Core;

/// <summary>极简本地日志（app.log，追加写）。业务数据仍走 AtomicJson。</summary>
public sealed class Logger
{
    static readonly object Sync = new();
    readonly string _path;
    public bool Enabled { get; set; } = true;

    public Logger(DataPaths paths) => _path = paths.Log;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    void Write(string level, string message)
    {
        if (!Enabled) return;
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line, Encoding.UTF8);
                var info = new FileInfo(_path);
                if (info.Length > 512 * 1024)   // 超过 512KB 截断
                {
                    var text = File.ReadAllText(_path);
                    var keep = Math.Min(text.Length, 64 * 1024);
                    File.WriteAllText(_path, text.Substring(text.Length - keep));
                }
            }
            catch { /* 日志失败不能影响业务 */ }
        }
    }
}

/// <summary>缓动函数（UI 动画与自绘共用）。</summary>
public static class Easing
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double OutQuint(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 5);
}
