using ClassDailyLand.Core;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Storage;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassDailyLand.App.Integrations.Audio;

/// <summary>主岛右侧媒体片段要显示的内容。</summary>
public sealed record MediaDisplay(
    string Title,
    string Artist,
    bool Playing,
    ImageSource? Artwork,
    double Width);

/// <summary>
/// 媒体集成：把 SMTC 采样翻译成"要不要显示、显示什么"。
///
/// 规则（与 Python 版一致）：
///   · 播放中一直显示；
///   · 暂停/停止后仍保留 <c>hide_after_sec</c> 秒，然后隐藏；
///   · **上课期间整块隐藏并停掉后台轮询**，下课后自动恢复（省电也避免干扰）。
///
/// 设置项（settings.json）：
///   audio_enabled / audio_show_on(main|sub) / audio_hide_after_sec / audio_max_width / audio_show_title
/// </summary>
public sealed class AudioService : IDisposable
{
    readonly SmtcBridge _bridge;
    readonly Logger _log;
    readonly string _artPath;

    MediaInfo? _current;
    MediaInfo? _last;
    DateTime _lastPlayingUtc = DateTime.MinValue;
    bool _inClass;
    int _loadedRev = -1;
    ImageSource? _artwork;

    public AudioService(DataPaths paths, Logger log)
    {
        _log = log;
        _artPath = Path.Combine(paths.Root, "integrations", "audio", "art.png");
        _bridge = new SmtcBridge(Path.GetDirectoryName(_artPath)!, log);
        _bridge.Sampled += OnSampled;
    }

    /// <summary>桥不可用（找不到 powershell.exe）时整个功能自动降级。</summary>
    public static bool Available => SmtcBridge.Available;

    public bool Enabled { get; private set; } = true;
    public bool ShowOnMain => ShowOn != "sub";
    public string ShowOn { get; private set; } = "main";
    public int HideAfterSec { get; private set; } = 5;
    public int MaxWidth { get; private set; } = 240;
    public bool ShowTitle { get; private set; } = true;

    /// <summary>当前是否真的占着显示位（设置页状态行也用它）。</summary>
    public bool HasMedia => Display is not null;

    /// <summary>给设置页看的一行状态。</summary>
    public string StatusText
    {
        get
        {
            if (!SmtcBridge.Available) return "媒体桥不可用：找不到 Windows PowerShell。";
            if (!Enabled) return "已停用媒体显示。";
            if (_inClass) return "上课中：已隐藏并停止轮询。";
            var info = _current;
            if (info is { HasContent: true })
                return $"当前媒体：{info.Title}" +
                       (string.IsNullOrEmpty(info.Artist) ? "" : $"（{info.Artist}）") +
                       (info.Playing ? " · 播放中" : " · 已暂停");
            return "当前没有正在播放的媒体。";
        }
    }

    // ————————————————————— 设置 —————————————————————

    public void ApplySettings(SettingsManager s)
    {
        Enabled = s.Bool("audio_enabled");
        ShowOn = string.Equals(s.Str("audio_show_on"), "sub", StringComparison.OrdinalIgnoreCase) ? "sub" : "main";
        HideAfterSec = Math.Clamp(s.Int("audio_hide_after_sec"), 0, 120);
        MaxWidth = Math.Clamp(s.Int("audio_max_width"), 120, 400);
        ShowTitle = s.Bool("audio_show_title");
    }

    // ————————————————————— 每秒驱动 —————————————————————

    /// <summary>每秒调用一次：决定轮询开关，并维护"最后播放时刻"。</summary>
    public void Tick(bool inClass)
    {
        _inClass = inClass;
        var shouldRun = Enabled && !_inClass && SmtcBridge.Available;

        if (shouldRun && !_bridge.Running) _bridge.Start();
        else if (!shouldRun && _bridge.Running) _bridge.Stop();

        if (!shouldRun && !_testMode) _current = null;

        if (_current is { Playing: true, HasContent: true })
        {
            _last = _current;
            _lastPlayingUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 当前要显示在岛上的内容；null = 不占地方。
    /// 只在 UI 线程取（会读封面文件并建 BitmapImage）。
    /// </summary>
    public MediaDisplay? Display
    {
        get
        {
            if (!Enabled || _inClass) return null;
            if (!SmtcBridge.Available && !_testMode) return null;

            var info = _current;
            var playing = info is { Playing: true, HasContent: true };

            if (!playing)
            {
                // 暂停 / 停止：胶囊上的媒体片段再留 HideAfterSec 秒（默认 5s）就整段收起。
                //
                // ★ 这里的关键是"暂停时 SMTC 依然报着 title/artist"——
                //   所以不能靠 info 是否为空来判断"还要不要显示"，
                //   必须看**最后一次真正在播放的时刻**（_lastPlayingUtc，只在 playing 时刷新）。
                //   否则暂停后 info 一直有内容，片段就永远收不起来了。
                var keep = HideAfterSec;
                var carry = info is { HasContent: true } ? info : _last;
                if (keep <= 0 || carry is not { HasContent: true }) return null;
                if ((DateTime.UtcNow - _lastPlayingUtc).TotalSeconds > keep) return null;
                info = carry;
            }

            var item = info!;           // 上面两条分支都保证了非空
            return new MediaDisplay(
                ShowTitle ? item.Title : "正在播放",
                ShowTitle ? item.Artist : "",
                playing,                 // 暂停残留那条显示▶，重新播放后自动变回⏸
                ArtworkFor(item),
                MaxWidth);
        }
    }

    /// <summary>副岛用的内容（audio_show_on=sub 时）；(标题, 值)。</summary>
    public (string Title, string Value)? SubIsland
    {
        get
        {
            if (ShowOnMain) return null;
            var d = Display;
            if (d is null) return null;
            var value = string.IsNullOrEmpty(d.Artist) ? d.Title : $"{d.Title} · {d.Artist}";
            return ("♪", value);
        }
    }

    public void TogglePlayPause()
    {
        if (_testMode)
        {
            // 测试媒体没有真实的播放器可切换，就地翻转一下，方便看两种状态。
            // 恢复播放要立刻刷新计时，否则"5 秒收起"的判定会慢一拍。
            _current = _current is null ? null : _current with { Playing = !_current.Playing };
            if (_current is { Playing: true })
            {
                _last = _current;
                _lastPlayingUtc = DateTime.UtcNow;
            }
            return;
        }
        _bridge.TogglePlayPause();
    }

    // ————————————————————— 测试注入 —————————————————————
    // 没有正在播放的音乐时，用它预览媒体片段长什么样（对齐 Python 版的"注入测试媒体"）。

    bool _testMode;

    public bool IsTestMedia => _testMode;

    public void InjectTestMedia(string title = "测试歌曲", string artist = "测试歌手")
    {
        _testMode = true;
        _current = new MediaInfo(title, artist, true, 0, null);
        _last = _current;
        _lastPlayingUtc = DateTime.UtcNow;
    }

    public void ClearTestMedia()
    {
        _testMode = false;
        _current = null;
        _last = null;
        _lastPlayingUtc = DateTime.MinValue;
    }

    public void Shutdown() => _bridge.Stop();

    // ————————————————————— 封面 —————————————————————

    /// <summary>封面按 <see cref="MediaInfo.Revision"/> 缓存，换歌才重新解码。</summary>
    ImageSource? ArtworkFor(MediaInfo info)
    {
        if (info.Revision == _loadedRev) return _artwork;
        _loadedRev = info.Revision;
        _artwork = null;

        var path = _bridge.ArtPath;
        if (!File.Exists(path)) return null;

        try
        {
            // OnLoad + Freeze：立刻读完并冻结，不锁文件、可跨线程复用
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            _artwork = bmp;
        }
        catch (Exception ex)
        {
            _log.Warn("读取封面失败：" + ex.Message);
        }
        return _artwork;
    }

    void OnSampled(MediaInfo info)
    {
        // 后台线程 → 只存字段；界面在下一个 tick 自己去取。
        // 测试注入期间忽略真实采样，否则预览会被真播放的内容顶掉。
        if (_testMode) return;
        _current = info.HasContent ? info : null;
    }

    public void Dispose() => _bridge.Dispose();
}
