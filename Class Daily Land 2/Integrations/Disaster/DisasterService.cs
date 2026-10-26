using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Integrations.Disaster;

/// <summary>
/// 灾害预警集成：地震预警（ICL 主源 + Wolfx 备源）与气象灾害（中央气象台）。
///
/// <para>移植自 Python 版 <c>builtin/disaster/main.py</c>，显示位换成这里的形式：</para>
/// <list type="bullet">
///   <item><b>主岛</b>：地震时用「红色倒计时条 + 灾害预警文案」接管（复用上课倒计时那套渲染）；</item>
///   <item><b>副岛</b>：横波倒计时 / 气象预警摘要；</item>
///   <item><b>下拉面板</b>：震中、震级、发震时刻、距离、烈度、最近县，或气象预警清单；</item>
///   <item><b>提醒</b>：托盘气泡（同一事件按 uid 去重 + 节流，等级升级会再提醒一次）。</item>
/// </list>
///
/// 所有网络请求 10 秒超时、失败指数退避；数据留在内存 + cache.json，重启后先显示缓存。
/// </summary>
public sealed class DisasterService : IDisposable
{
    const double CacheSaveIntervalSec = 30;

    readonly Dispatcher _dispatcher;
    readonly Logger _log;
    readonly SettingsManager _settings;
    readonly string _cachePath;
    readonly Func<bool> _isInClass;
    readonly Action<string, string> _notify;

    // ————————————————————— 配置 —————————————————————
    public bool Enabled { get; private set; } = true;
    public bool QuakeEnabled { get; private set; } = true;
    public bool WeatherEnabled { get; private set; } = true;
    public string Province { get; private set; } = "四川省";
    public string City { get; private set; } = "";
    public string District { get; private set; } = "";
    public double? Lat { get; private set; }
    public double? Lon { get; private set; }
    public double Threshold { get; private set; } = 2.0;
    public int QuakeIntervalSec { get; private set; } = 5;
    public int WeatherIntervalSec { get; private set; } = 60;
    public bool NotifyEnabled { get; private set; } = true;
    public bool BannerEnabled { get; private set; } = true;
    public int SmallHoldSec { get; private set; } = 5;
    public bool ShowStatusCard { get; private set; } = true;

    /// <summary>用户点过「确认」的事件（uid + revision），不再弹警报条。</summary>
    (string Uid, int Revision)? _dismissed;

    // ————————————————————— 状态 —————————————————————
    readonly object _gate = new();
    Dictionary<string, QuakeEvent> _quakes = new(StringComparer.Ordinal);
    Dictionary<string, WeatherEvent> _weather = new(StringComparer.Ordinal);
    readonly Dictionary<string, (double Ts, string Level)> _notified = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _firstSeen = new(StringComparer.Ordinal);

    readonly SourceStatus _icl = new("icl", "ICL 地震");
    readonly SourceStatus _wolfx = new("wolfx", "Wolfx 地震");
    readonly SourceStatus _nmc = new("nmc", "中央气象台");

    bool _iclInFlight, _wolfxInFlight, _nmcInFlight;
    double _lastIclPoll, _lastWeatherPoll, _lastCacheSave;
    int _wolfxFailStreak;
    bool _cacheDirty;

    public QuakeEvaluation? Focus { get; private set; }
    public IReadOnlyList<object> Others { get; private set; } = Array.Empty<object>();
    public IslandEmergency? Emergency { get; private set; }

    public DisasterService(
        Dispatcher dispatcher,
        SettingsManager settings,
        Logger log,
        string cachePath,
        Func<bool> isInClass,
        Action<string, string> notify)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _log = log;
        _cachePath = cachePath;
        _isInClass = isInClass;
        _notify = notify;
        LoadCache();
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ————————————————————— 设置 —————————————————————

    public void ApplySettings()
    {
        Enabled = _settings.Bool("disaster_enabled");
        QuakeEnabled = _settings.Bool("disaster_quake_enabled");
        WeatherEnabled = _settings.Bool("disaster_weather_enabled");
        NotifyEnabled = _settings.Bool("disaster_notify_enabled");
        BannerEnabled = _settings.Bool("disaster_banner_enabled");
        ShowStatusCard = _settings.Bool("disaster_show_status_card");
        Province = _settings.Str("disaster_province");
        City = _settings.Str("disaster_city");
        District = _settings.Str("disaster_district");
        Threshold = Math.Clamp(_settings.Int("disaster_threshold_x10"), 0, 120) / 10.0;
        QuakeIntervalSec = Math.Clamp(_settings.Int("disaster_quake_interval_sec"), 3, 120);
        WeatherIntervalSec = Math.Clamp(_settings.Int("disaster_weather_interval_sec"), 30, 3600);
        SmallHoldSec = Math.Clamp(_settings.Int("disaster_small_hold_sec"), 0, 120);

        // 坐标：显式填了就用显式的，否则按 省/市/区县 查表
        var latText = _settings.Str("disaster_lat");
        var lonText = _settings.Str("disaster_lon");
        if (double.TryParse(latText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var la) &&
            double.TryParse(lonText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lo) &&
            la is >= -90 and <= 90 && lo is >= -180 and <= 180)
        {
            Lat = la;
            Lon = lo;
        }
        else
        {
            // 按 省/市/区县 查表；只选了省没选市时，退到省会（省的第一个地级市）。
            // 没有这一步，只选「四川省」的用户会拿不到坐标，倒计时永远算不出来。
            var coord = Cities.CoordOf(Province, City, District);
            if (coord is null && !string.IsNullOrWhiteSpace(Province))
            {
                var capital = Cities.CitiesOf(Province).FirstOrDefault();
                if (capital is not null) coord = Cities.CoordOf(Province, capital);
            }
            Lat = coord?.Lat;
            Lon = coord?.Lon;
        }

        _icl.Enabled = QuakeEnabled;
        _wolfx.Enabled = QuakeEnabled;
        _nmc.Enabled = WeatherEnabled && !string.IsNullOrEmpty(Province) && Province != Cities.NoWeather;
    }

    // ————————————————————— 每秒 tick —————————————————————

    /// <summary>由 AppController 的 1 秒 tick 驱动。</summary>
    public void Tick()
    {
        if (!Enabled) return;
        var now = Now;

        PollIclIfDue(now);
        PollWolfxIfDue(now);
        PollWeatherIfDue(now);

        RefreshFocus(now);
        SaveCacheIfDue(now);
    }

    // ————————————————————— 轮询 —————————————————————

    void PollIclIfDue(double now)
    {
        if (!QuakeEnabled || _iclInFlight) return;
        if (now - _lastIclPoll < QuakeIntervalSec || !_icl.Ready(now)) return;

        _lastIclPoll = now;
        _iclInFlight = true;
        var startMs = (long)((now - 7200) * 1000);
        var url = string.Format(DisasterSources.IclListUrl, startMs);

        _ = Task.Run(async () =>
        {
            var (doc, error) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
            if (doc is null)
            {
                Post(() =>
                {
                    _icl.OnFail(error, Now);
                    _iclInFlight = false;
                });
                return;
            }

            using (doc)
            {
                var events = DisasterSources.ParseIclEvents(doc.RootElement);
                Post(() =>
                {
                    _icl.OnOk(Now);
                    _iclInFlight = false;
                    _wolfxFailStreak = 0;
                    IngestQuakes(events);
                });
            }
        });
    }

    void PollWolfxIfDue(double now)
    {
        // ICL 连续失败两次后，改用 Wolfx 顶上（与 Python 版一致的降级策略）
        if (!QuakeEnabled || _wolfxInFlight) return;
        if (_wolfxFailStreak < 2 && _icl.Ok != false) return;
        if (now - _lastIclPoll < QuakeIntervalSec || !_wolfx.Ready(now)) return;

        _lastIclPoll = now;
        _wolfxInFlight = true;

        _ = Task.Run(async () =>
        {
            var all = new List<QuakeEvent>();
            var errors = new List<string>();
            foreach (var (key, url) in DisasterSources.WolfxEewUrls)
            {
                var (doc, error) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
                if (doc is null)
                {
                    errors.Add($"{key}: {error}");
                    continue;
                }
                using (doc)
                {
                    all.AddRange(DisasterSources.ParseWolfxEvents(doc.RootElement, key));
                }
            }

            if (all.Count == 0 && errors.Count == DisasterSources.WolfxEewUrls.Count)
            {
                Post(() =>
                {
                    _wolfx.OnFail(string.Join("; ", errors), Now);
                    _wolfxFailStreak++;
                    _wolfxInFlight = false;
                });
                return;
            }

            Post(() =>
            {
                _wolfx.OnOk(Now);
                _wolfxInFlight = false;
                _wolfxFailStreak = 0;
                IngestQuakes(all);
            });
        });
    }

    void PollWeatherIfDue(double now)
    {
        if (!WeatherEnabled || _nmcInFlight) return;
        if (string.IsNullOrEmpty(Province) || Province == Cities.NoWeather) return;
        if (now - _lastWeatherPoll < WeatherIntervalSec || !_nmc.Ready(now)) return;

        _lastWeatherPoll = now;
        _nmcInFlight = true;
        var url = DisasterSources.NmcUrl(Province);

        _ = Task.Run(async () =>
        {
            var (doc, error) = await DisasterSources.FetchJsonAsync(url, DisasterSources.NmcHeaders)
                .ConfigureAwait(false);
            if (doc is null)
            {
                Post(() =>
                {
                    _nmc.OnFail(error, Now);
                    _nmcInFlight = false;
                });
                return;
            }

            using (doc)
            {
                var alerts = DisasterSources.ParseNmcAlerts(doc.RootElement, Province, strict: false);
                Post(() =>
                {
                    _nmc.OnOk(Now);
                    _nmcInFlight = false;
                    lock (_gate)
                    {
                        var map = _weather.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                        foreach (var a in alerts) map[a.Uid] = a;
                        _weather = DisasterSources.PruneWeather(map, Now);
                        _cacheDirty = true;
                    }
                });
            }
        });
    }

    void IngestQuakes(IEnumerable<QuakeEvent> incoming)
    {
        lock (_gate)
        {
            var (merged, _) = DisasterSources.MergeQuakes(_quakes, incoming.ToList());
            _quakes = DisasterSources.PruneQuakes(merged, Now);
            _cacheDirty = true;
        }
        FetchDetailsIfNeeded();
    }

    /// <summary>新事件补一次 ICL 详情（取逐县距离，面板里显示"最近县"）。</summary>
    void FetchDetailsIfNeeded()
    {
        List<QuakeEvent> need;
        lock (_gate)
        {
            need = _quakes.Values
                .Where(q => q.Source == "icl" && q.Counties is null && q.Revision >= 1)
                .Take(2)
                .ToList();
        }
        if (need.Count == 0) return;

        foreach (var ev in need)
        {
            var url = string.Format(DisasterSources.IclDetailUrl, ev.EventId);
            _ = Task.Run(async () =>
            {
                var (doc, _) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
                if (doc is null) return;
                using (doc)
                {
                    var counties = DisasterSources.ParseIclDetail(doc.RootElement);
                    if (counties.Count == 0) return;
                    Post(() =>
                    {
                        lock (_gate)
                        {
                            if (_quakes.TryGetValue(ev.Uid, out var old))
                                _quakes[ev.Uid] = old with { Counties = counties };
                        }
                    });
                }
            });
        }
    }

    void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    // ————————————————————— 选主 + 展示 —————————————————————

    void RefreshFocus(double now)
    {
        var evaluations = EvaluateQuakes(now);
        Focus = evaluations.FirstOrDefault();
        Others = evaluations.Skip(1).Take(4).Cast<object>().ToList();

        if (Focus is null)
        {
            var weather = WeatherForIsland();
            if (weather is not null)
            {
                Focus = null;
                _weatherFocus = weather;
            }
            else _weatherFocus = null;
        }
        else _weatherFocus = null;

        UpdateEmergency(now);
        MaybeNotify(now);
    }

    WeatherEvent? _weatherFocus;

    List<QuakeEvaluation> EvaluateQuakes(double now)
    {
        if (!QuakeEnabled) return new List<QuakeEvaluation>();
        if (Lat is null || Lon is null) return new List<QuakeEvaluation>();

        List<QuakeEvent> events;
        lock (_gate) events = _quakes.Values.ToList();

        var list = new List<QuakeEvaluation>();
        foreach (var ev in events)
        {
            var r = QuakeCalc.Evaluate(ev, Lat, Lon, now, Threshold);
            if (r is { Displayable: true }) list.Add(r);
        }

        return list
            .OrderBy(e => e.Triggered ? 0 : 1)
            .ThenByDescending(e => e.LocalIntensity)
            .ThenBy(e => e.RemainSec)
            .ToList();
    }

    /// <summary>气象：设了城市只显示本市；否则显示全省。</summary>
    WeatherEvent? WeatherForIsland()
    {
        if (!WeatherEnabled) return null;
        if (string.IsNullOrEmpty(Province) || Province == Cities.NoWeather) return null;

        List<WeatherEvent> all;
        lock (_gate) all = _weather.Values.ToList();

        var key = Cities.ShortName(City);
        var local = all.Where(w => QuakeCalc.LevelAtLeast(w.Level, "yellow") &&
                                   (key.Length > 0 ? w.Title.Contains(key, StringComparison.Ordinal) : true))
            .ToList();

        if (key.Length > 0 && local.Count == 0) return null;

        return local
            .OrderByDescending(w => QuakeCalc.Rank(w.Level))
            .ThenByDescending(w => w.IssuedTs ?? 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// 主岛的紧急倒计时：地震时接管。
    /// 倒计时条一律红色（用户要求），比例按「剩余 / 总倒计时」收缩，
    /// 直接复用上课倒计时那套蓝条的渲染。
    /// </summary>
    void UpdateEmergency(double now)
    {
        Emergency = null;
        if (!Enabled || !BannerEnabled) return;

        var focus = Focus;
        if (focus is null) return;
        if (!focus.Displayable) return;

        // 用户点过确认的事件不再弹（升级 revision 后会重新出现）
        if (_dismissed is { } d && d.Uid == focus.Uid && focus.Event.Revision <= d.Revision) return;

        var big = QuakeCalc.Rank(focus.Level) >= QuakeCalc.Rank(QuakeLevels.Red);
        if (!big)
        {
            // 小震：只短暂占用，之后把课程显示交还（倒计时仍在副岛/面板里）
            if (!_firstSeen.TryGetValue(focus.Uid, out var seenAt))
            {
                seenAt = now;
                _firstSeen[focus.Uid] = now;
            }
            if (SmallHoldSec <= 0) return;
            if (now - seenAt > SmallHoldSec) return;
        }

        var window = Math.Max(1, focus.CountdownSec);
        var remain = Math.Max(0, focus.ArriveTs - now);
        var ratio = focus.Incoming && remain > 0 ? Math.Clamp(remain / window, 0, 1) : 0;

        Emergency = new IslandEmergency(
            Text: EmergencyText(focus),
            Ratio: ratio,
            BarColor: Color.FromRgb(0xFF, 0x45, 0x3A),   // 地震预警固定红条
            Level: focus.Level);
    }

    static string EmergencyText(QuakeEvaluation focus)
    {
        var ev = focus.Event;
        var place = string.IsNullOrWhiteSpace(ev.Epicenter) ? "未知震中" : ev.Epicenter.Trim();
        var mag = ev.Magnitude;
        if (focus.Incoming)
        {
            var sec = Math.Max(0, (int)Math.Round(focus.ArriveTs - Now));
            return $"灾害预警 · {place} M{mag:0.#} · 横波还有 {sec} 秒";
        }
        return $"灾害预警 · {place} M{mag:0.#} · 横波已到达";
    }

    public void DismissEmergency()
    {
        if (Focus is null) return;
        _dismissed = (Focus.Uid, Focus.Event.Revision);
        Emergency = null;
        _log.Info("灾害预警：用户已确认，收起倒计时");
    }

    // ————————————————————— 副岛 / 面板 —————————————————————

    /// <summary>副岛显示（地震优先，其次气象）。</summary>
    public (string Title, string Value, double Progress)? SubIsland
    {
        get
        {
            if (!Enabled) return null;
            var focus = Focus;
            if (focus is not null)
            {
                var sec = Math.Max(0, (int)Math.Round(focus.ArriveTs - Now));
                var title = focus.Incoming ? "地震预警" : "地震";
                var value = focus.Incoming
                    ? $"{sec} 秒 · 烈度{focus.LocalIntensity:0.#}"
                    : $"已到达 · 烈度{focus.LocalIntensity:0.#}";
                var window = Math.Max(1, focus.CountdownSec);
                var ratio = focus.Incoming ? Math.Clamp((focus.ArriveTs - Now) / window, 0, 1) : 0;
                return (title, value, ratio);
            }

            var weather = _weatherFocus;
            if (weather is not null)
                return ("气象", $"{weather.Signal}{QuakeCalc.LevelWord(weather.Level)}", 0);

            return null;
        }
    }

    /// <summary>下拉面板（和插件面板并排显示）。</summary>
    public (string Title, IReadOnlyList<string> Lines)? Panel
    {
        get
        {
            if (!Enabled) return null;
            var lines = PanelLines();
            return lines.Count == 0 ? null : ("灾害预警", lines);
        }
    }

    List<string> PanelLines()
    {
        var lines = new List<string>();
        var focus = Focus;

        if (focus is not null)
        {
            var ev = focus.Event;
            lines.Add($"地震 · {(ev.Source == "drill" ? "演练" : ev.Source == "icl" ? "ICL" : "Wolfx")}");
            lines.Add($"M{ev.Magnitude:0.#} {ev.Epicenter}");
            lines.Add($"发震 {QuakeCalc.FmtClock(ev.OriginTs)} · 深 {ev.Depth:0}km");
            lines.Add($"本地 {focus.DistanceKm:0.#}km · 烈度 {focus.LocalIntensity:0.#}");
            lines.Add(focus.Incoming
                ? $"横波还有 {Math.Max(0, (int)Math.Round(focus.ArriveTs - Now))} 秒到达"
                : "横波已到达");

            var near = ev.Counties?.OrderBy(c => c.Distance ?? double.MaxValue).FirstOrDefault();
            if (near is not null)
                lines.Add($"最近县 {near.Name} {near.Distance:0}km 烈度{near.Intensity:0.#}");

            if (Others.Count > 0)
                lines.Add($"另有 {Others.Count} 条地震事件");
        }
        else if (_weatherFocus is { } w)
        {
            lines.Add($"气象灾害 · {Province}");
            if (!string.IsNullOrEmpty(City)) lines.Add($"本市 {City}");
            lines.Add($"{w.County} · {w.Signal}{QuakeCalc.LevelWord(w.Level)}");
            lines.Add($"发布 {QuakeCalc.FmtClockHm(w.IssuedTs)}");
        }
        else if (!ShowStatusCard)
        {
            return lines;
        }
        else
        {
            lines.Add("灾害预警 · 监测中");
        }

        lines.Add(_icl.Describe(Now));
        if (WeatherEnabled && !string.IsNullOrEmpty(Province) && Province != Cities.NoWeather)
            lines.Add(_nmc.Describe(Now));

        if (Lat is null || Lon is null)
            lines.Add("未设置坐标 · 无法计算地震倒计时");
        else
            lines.Add($"坐标 {Lat:0.##}, {Lon:0.##} · 阈值 {Threshold:0.#}");

        if (_isInClass()) lines.Add("上课中 · 气象低等级已静默");
        return lines;
    }

    // ————————————————————— 提醒 —————————————————————

    void MaybeNotify(double now)
    {
        if (!NotifyEnabled || Focus is null) return;

        var focus = Focus!;
        var ev = focus.Event;
        if (!focus.Triggered) return;

        var throttle = Math.Max(5, _settings.Int("disaster_throttle_sec"));
        if (_notified.TryGetValue(ev.Uid, out var last))
        {
            var upgraded = QuakeCalc.Rank(focus.Level) > QuakeCalc.Rank(last.Level);
            if (!upgraded && now - last.Ts < throttle) return;
        }

        var sec = Math.Max(0, (int)Math.Round(focus.ArriveTs - now));
        _notify("灾害预警",
            $"{ev.Epicenter} M{ev.Magnitude:0.#} · 横波还有 {sec} 秒 · 预估烈度{focus.LocalIntensity:0.#}");
        _notified[ev.Uid] = (now, focus.Level);

        var live = _quakes.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in _notified.Keys.ToList())
            if (!live.Contains(key)) _notified.Remove(key);
    }

    // ————————————————————— 演练 / 状态 —————————————————————

    /// <summary>
    /// 注入一次演练地震（设置页的「模拟一次」用它）。
    /// 震中默认取当前配置的坐标往西 120km，这样倒计时是个像样的十几秒。
    /// </summary>
    public string StartDrill(double? magnitude = 6.4, double? depth = 10)
    {
        var lat = Lat ?? 30.6595;   // 成都
        var lon = Lon ?? 104.0658;
        var now = Now;

        var ev = new QuakeEvent(
            Uid: "drill:" + (long)now,
            Source: "drill",
            EventId: "drill",
            Revision: 1,
            OriginTs: now - 3,
            UpdatedTs: now,
            Lat: lat + 0.6,
            Lon: lon - 1.1,
            Depth: depth ?? 10,
            Magnitude: magnitude ?? 6.4,
            Epicenter: "演练震中");

        lock (_gate)
        {
            _quakes[ev.Uid] = ev;
        }
        RefreshFocus(now);
        return $"已注入演练地震 M{ev.Magnitude:0.#}（震中 演练震中），当前倒计时 " +
               $"{(Focus is null ? "未达显示条件" : Math.Max(0, (int)Math.Round(Focus.ArriveTs - now)) + " 秒")}";
    }

    public void ClearDrill()
    {
        lock (_gate)
        {
            foreach (var key in _quakes.Keys.Where(k => k.StartsWith("drill:", StringComparison.Ordinal)).ToList())
                _quakes.Remove(key);
        }
        _firstSeen.Clear();
        Focus = null;
        Emergency = null;
        _dismissed = null;
    }

    /// <summary>设置页的状态说明（多行）。</summary>
    public string StatusText
    {
        get
        {
            var now = Now;
            var lines = new List<string>
            {
                Lat is null || Lon is null
                    ? "坐标未设置：请在下面的「位置」里选省市（或手填经纬度）。"
                    : $"当前坐标 {Lat:0.##}, {Lon:0.##} · 烈度阈值 {Threshold:0.#}",
                _icl.Describe(now),
                _nmc.Describe(now),
            };
            if (Focus is not null)
            {
                var sec = Math.Max(0, (int)Math.Round(Focus.ArriveTs - now));
                lines.Add($"当前震情：{Focus.Event.Epicenter} M{Focus.Event.Magnitude:0.#} · " +
                          (Focus.Incoming ? $"横波还有 {sec} 秒" : "横波已到达"));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ————————————————————— 缓存 —————————————————————
    // 只是让重启后能先显示上一次的震情；坏了就丢掉，不影响运行。

    void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var dto = System.Text.Json.JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(_cachePath));
            if (dto?.Quakes is null) return;
            foreach (var q in dto.Quakes) _quakes[q.Uid] = q;
        }
        catch (Exception ex)
        {
            _log.Warn("读灾害缓存失败：" + ex.Message);
        }
    }

    void SaveCacheIfDue(double now)
    {
        if (!_cacheDirty) return;
        if (now - _lastCacheSave < CacheSaveIntervalSec) return;
        _lastCacheSave = now;
        _cacheDirty = false;

        try
        {
            var dto = new CacheDto { Quakes = _quakes.Values.TakeLast(8).ToList() };
            Core.Storage.AtomicJson.Write(_cachePath, dto);
        }
        catch (Exception ex)
        {
            _log.Warn("写灾害缓存失败：" + ex.Message);
        }
    }

    sealed class CacheDto
    {
        public List<QuakeEvent>? Quakes { get; set; }
    }

    public void Dispose() { }
}
