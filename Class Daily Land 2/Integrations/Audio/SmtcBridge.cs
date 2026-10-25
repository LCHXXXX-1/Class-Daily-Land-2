using ClassDailyLand.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClassDailyLand.App.Integrations.Audio;

/// <summary>一次 SMTC 采样结果。<see cref="Revision"/> 变化表示封面文件换过了。</summary>
public sealed record MediaInfo(string Title, string Artist, bool Playing, int Revision, string? ArtPath)
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Windows 媒体控制（SMTC）桥。
///
/// <para><b>为什么走 PowerShell 而不是 P/Invoke</b>：<c>GlobalSystemMediaTransportControlsSessionManager</c>
/// 是 WinRT 类型，纯 P/Invoke 拿不到（要做 CsWinRT ABI 互操作，还得给工程加 Windows SDK 投影）。
/// 用 <c>powershell.exe</c> 跑一小段 WinRT 脚本是**零依赖且 Win10 1809+ 通用**的办法 ——
/// 这也是 Python 版验证过的路径。</para>
///
/// <para>性能取舍：常驻一个 PowerShell 进程，每秒输出一行 JSON。上课期间会被
/// <see cref="AudioService"/> 停掉，所以不是一直占着。</para>
/// </summary>
public sealed class SmtcBridge : IDisposable
{
    readonly string _dir;
    readonly Logger _log;
    Process? _poll;
    Thread? _reader;
    volatile bool _running;
    int _generation;

    public SmtcBridge(string directory, Logger log)
    {
        _dir = directory;
        _log = log;
        try { Directory.CreateDirectory(_dir); } catch { /* 建不出来就在 Start 里报错 */ }
    }

    public string ArtPath => Path.Combine(_dir, "art.png");
    public bool Running => _running;

    /// <summary>可用性：找不到 powershell.exe 时整个功能自动降级（界面会说明原因）。</summary>
    public static bool Available => File.Exists(PowerShellPath);

    static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>每采样到一行就回调（**在后台线程**，调用方自己切 UI 线程）。</summary>
    public event Action<MediaInfo>? Sampled;

    // ————————————————————— 启停 —————————————————————

    public void Start()
    {
        if (_running || !Available) return;
        var script = EnsureScript(PollScript, "poll.ps1");
        if (script is null) return;

        try
        {
            var psi = new ProcessStartInfo(PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-ParentPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("-ArtPath");
            psi.ArgumentList.Add(ArtPath);

            _poll = Process.Start(psi);
            if (_poll is null) return;
            _running = true;
            var gen = ++_generation;

            _reader = new Thread(() => ReadLoop(_poll, gen))
            {
                IsBackground = true,
                Name = "smtc-reader",
            };
            _reader.Start();
            _log.Info("媒体桥：已启动 SMTC 轮询");
        }
        catch (Exception ex)
        {
            _log.Warn("媒体桥启动失败：" + ex.Message);
            Kill();
        }
    }

    public void Stop()
    {
        if (!_running && _poll is null) return;
        _running = false;
        _generation++;
        Kill();
        _log.Info("媒体桥：已停止 SMTC 轮询");
    }

    void Kill()
    {
        try
        {
            if (_poll is { HasExited: false }) _poll.Kill(entireProcessTree: true);
        }
        catch { /* 已经退出 */ }
        try { _poll?.Dispose(); } catch { }
        _poll = null;
        _reader = null;
    }

    void ReadLoop(Process process, int generation)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = process.StandardOutput.ReadLine();
                if (line is null) break;
                if (generation != _generation) break;
                line = line.Trim();
                if (line.Length == 0 || line[0] != '{') continue;

                var info = Parse(line);
                if (info is not null) Sampled?.Invoke(info);
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation) _log.Warn("媒体桥读取异常：" + ex.Message);
        }
        finally
        {
            if (generation == _generation && _running)
            {
                // 脚本意外退出（比如 WinRT 初始化失败）：标记停止，下次 tick 会重试
                _running = false;
                Kill();
            }
        }
    }

    static MediaInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = Str(root, "title");
            var artist = Str(root, "artist");
            var status = Str(root, "status");
            var rev = root.TryGetProperty("rev", out var r) && r.TryGetInt32(out var n) ? n : 0;
            var art = Str(root, "art");
            return new MediaInfo(
                title,
                artist,
                string.Equals(status, "playing", StringComparison.OrdinalIgnoreCase),
                rev,
                art.Length == 0 ? null : art);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    // ————————————————————— 播放/暂停 —————————————————————

    /// <summary>切换播放/暂停（异步，最多等 15 秒；不阻塞 UI）。</summary>
    public void TogglePlayPause()
    {
        if (!Available) return;
        var script = EnsureScript(ToggleScript, "toggle.ps1");
        if (script is null) return;

        var thread = new Thread(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(PowerShellPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
                    psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc is null) return;
                proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("媒体桥切换播放失败：" + ex.Message);
            }
        })
        { IsBackground = true, Name = "smtc-toggle" };
        thread.Start();
    }

    // ————————————————————— 脚本 —————————————————————

    string? EnsureScript(string content, string name)
    {
        try
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            _log.Warn($"媒体桥写脚本失败（{name}）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 轮询脚本：每秒输出一行 JSON（title/artist/status/rev/art）。
    /// 父进程退出时自己退出，避免留下孤儿 PowerShell。
    /// 换歌时把封面写成 art.png（先写 .tmp 再改名，读的一侧不会拿到半张图）。
    /// </summary>
    const string PollScript = """
param([int]$ParentPid = 0, [string]$ArtPath = "")

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $ty) {
  $m = $asTaskGeneric.MakeGenericMethod($ty)
  $t = $m.Invoke($null, @($op))
  $t.Wait(-1) | Out-Null
  $t.Result
}

$mgrType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
$propsType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime]
$streamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]

$mgr = Await ($mgrType::RequestAsync()) ($mgrType)
$rev = 0
$lastKey = ''

while ($true) {
  if ($ParentPid -gt 0) {
    $parent = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    if ($null -eq $parent) { exit }
  }

  $title = ''; $artist = ''; $status = 'none'
  try {
    $s = $mgr.GetCurrentSession()
    if ($s -ne $null) {
      $p = Await ($s.TryGetMediaPropertiesAsync()) ($propsType)
      $st = [int]$s.GetPlaybackInfo().PlaybackStatus
      if ($st -eq 4) { $status = 'playing' }
      elseif ($st -eq 5) { $status = 'paused' }
      else { $status = 'other' }
      $title = [string]$p.Title
      $artist = [string]$p.Artist

      $key = $title + '|' + $artist
      if ($ArtPath -ne '' -and $key -ne $lastKey) {
        $lastKey = $key
        $rev = $rev + 1
        $wrote = $false
        try {
          $thumb = $p.Thumbnail
          if ($thumb -ne $null) {
            $rs = Await ($thumb.OpenReadAsync()) ($streamType)
            if ($rs -ne $null -and $rs.Size -gt 0 -and $rs.Size -lt 8000000) {
              $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($rs)
              $ms = New-Object System.IO.MemoryStream
              $net.CopyTo($ms)
              $bytes = $ms.ToArray()
              $net.Dispose(); $ms.Dispose(); $rs.Dispose()
              $tmp = $ArtPath + '.tmp'
              [System.IO.File]::WriteAllBytes($tmp, $bytes)
              Move-Item -Force $tmp $ArtPath
              $wrote = $true
            }
          }
        } catch { }
        if (-not $wrote) { [System.IO.File]::Delete($ArtPath) }
      }
    }
  } catch { }

  $o = @{ title = $title; artist = $artist; status = $status; rev = $rev; art = $(if ($ArtPath -ne '' -and (Test-Path $ArtPath)) { $ArtPath } else { '' }) }
  [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress))
  [Console]::Out.Flush()
  Start-Sleep -Milliseconds 1000
}
""";

    /// <summary>只切换播放/暂停，跑完就退出（点一下岛上的按钮用）。</summary>
    const string ToggleScript = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $ty) {
  $m = $asTaskGeneric.MakeGenericMethod($ty)
  $t = $m.Invoke($null, @($op))
  $t.Wait(-1) | Out-Null
  $t.Result
}

$mgrType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
try {
  $mgr = Await ($mgrType::RequestAsync()) ($mgrType)
  $s = $mgr.GetCurrentSession()
  if ($s -ne $null) {
    $s.TryTogglePlayPauseAsync() | Out-Null
  }
} catch { }
""";

    public void Dispose() => Stop();
}
