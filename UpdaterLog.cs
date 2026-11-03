using System.Text;

namespace CDL2.Updater;

/// <summary>
/// 更新器自己的日志。拿到落点之前的行先攒在内存，Attach 时一次补写；
/// 写失败一律吞掉 —— 日志写不进去不能反过来把更新弄失败。
/// </summary>
public sealed class UpdaterLog
{
    private readonly object _gate = new();
    private readonly List<string> _pending = new();
    private string? _path;

    public string? Path => _path;

    public void Attach(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_gate)
        {
            _path = path;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, "===== 更新器启动 =====" + Environment.NewLine, Encoding.UTF8);

                if (_pending.Count > 0)
                {
                    File.AppendAllLines(path, _pending, Encoding.UTF8);
                    _pending.Clear();
                }
            }
            catch
            {
                // 落点不可写：继续攒在内存，至少本次会话内不丢
            }
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        lock (_gate)
        {
            if (_path is null)
            {
                _pending.Add(line);
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 静默：日志失败不影响更新
            }
        }
    }
}
