namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器自己的日志（<c>%LOCALAPPDATA%\ClassDailyLand\logs\updater-&lt;job_id&gt;.log</c>）。
///
/// 更新器没有界面，出问题时这就是唯一证据；所以：
///   · 拿到 log 路径之前的行先攒在内存里，<see cref="Attach"/> 时一次补写 —— 不丢启动阶段的信息；
///   · 写日志失败一律吞掉：日志写不进去不能反过来把更新弄失败。
/// </summary>
internal sealed class JobLog
{
    private readonly object _gate = new();
    private readonly List<string> _pending = new();
    private string? _path;

    /// <summary>绑定日志落点（通常在读到 job.json 之后）。</summary>
    public void Attach(string? path)
    {
        lock (_gate)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            foreach (var line in _pending) TryAppendLocked(line);
            _pending.Clear();
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (_gate)
        {
            if (_path is null) _pending.Add(line);
            else TryAppendLocked(line);
        }
    }

    private void TryAppendLocked(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_path!, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 见类注释：日志失败不影响更新
        }
    }
}
