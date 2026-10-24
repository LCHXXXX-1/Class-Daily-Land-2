using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassDailyLand.App.Integrations.Disaster;

/// <summary>
/// 灾害预警的数据源层（三个免 key 源）：
/// <list type="bullet">
///   <item>地震主源 ICL（成都高新减灾研究所，秒级，含逐县烈度/倒计时）</item>
///   <item>地震备源 Wolfx（四川 / 台网 / 福建 / 重庆 多源 + 速报列表补漏）</item>
///   <item>气象灾害 中央气象台 nmc.cn（按中文省名过滤）</item>
/// </list>
///
/// 解析函数都是纯函数（不碰网络/UI），便于单测；网络部分统一 8 秒超时。
/// </summary>
public static partial class DisasterSources
{
    const string UserAgent = "ClassBoard-DisasterAlert/1.0";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    static readonly HttpClient Http = CreateClient();

    public const string IclListUrl = "https://mobile-new.chinaeew.cn/v1/earlywarnings?updates=0&start_at={0}";
    public const string IclDetailUrl = "https://mobile-new.chinaeew.cn/v1/earlywarnings/{0}";

    public static readonly IReadOnlyDictionary<string, string> WolfxEewUrls = new Dictionary<string, string>
    {
        ["wolfx_sc"] = "https://api.wolfx.jp/sc_eew.json",
        ["wolfx_cenc"] = "https://api.wolfx.jp/cenc_eew.json",
        ["wolfx_fj"] = "https://api.wolfx.jp/fj_eew.json",
        ["wolfx_cq"] = "https://api.wolfx.jp/cq_eew.json",
    };

    public const string WolfxEqListUrl = "https://api.wolfx.jp/cenc_eqlist.json";

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.8");
        return client;
    }

    // ————————————————————— 网络 —————————————————————

    /// <summary>GET 一个 JSON（失败给出人话原因，不抛）。</summary>
    public static async Task<(JsonDocument? Doc, string Error)> FetchJsonAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers is not null)
                foreach (var kv in headers)
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0) return (null, "空响应");

            // 国内接口偶尔返回 GBK；先按 UTF-8 严格解，失败再试 GB18030
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding("GB18030").GetString(bytes);
            }

            if (string.IsNullOrWhiteSpace(text)) return (null, "空响应");
            return (JsonDocument.Parse(text), "");
        }
        catch (TaskCanceledException)
        {
            return (null, "请求超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return (null, ex.Message);
        }
    }

    public static string NmcUrl(string province) =>
        "http://www.nmc.cn/rest/findAlarm?pageNo=1&pageSize=50&signaltype=&signallevel=&province=" +
        Uri.EscapeDataString(province ?? "");

    public static string NmcNationwideUrl =>
        "http://www.nmc.cn/rest/findAlarm?pageNo=1&pageSize=200&signaltype=&signallevel=&province=";

    /// <summary>nmc 必须带这两个头，否则前置网关回 417。</summary>
    public static readonly IReadOnlyDictionary<string, string> NmcHeaders = new Dictionary<string, string>
    {
        ["X-Requested-With"] = "XMLHttpRequest",
        ["Referer"] = "http://www.nmc.cn/publish/alarm.html",
    };

    // ————————————————————— 解析工具 —————————————————————

    public static double? F(JsonElement? el)
    {
        if (el is null) return null;
        var v = el.Value;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return double.IsNaN(d) || double.IsInfinity(d) ? null : d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return double.IsNaN(p) || double.IsInfinity(p) ? null : p;
        return null;
    }

    public static string S(JsonElement? el, string fallback = "")
    {
        if (el is null) return fallback;
        var v = el.Value;
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.ToString(),
            _ => fallback,
        };
    }

    public static JsonElement? Prop(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v)) return v;
        return null;
    }

    [GeneratedRegex(@"(\d{4})[-/](\d{1,2})[-/](\d{1,2})[ T](\d{1,2}):(\d{1,2})(?::(\d{1,2}))?")]
    private static partial Regex BjTimePattern();

    /// <summary>"YYYY-MM-DD HH:MM:SS" 按北京时间解析成 epoch 秒。</summary>
    public static double? ParseBjTime(JsonElement? el)
    {
        var text = S(el);
        if (text.Length == 0) return null;
        var m = BjTimePattern().Match(text);
        if (!m.Success) return null;
        try
        {
            var dt = new DateTimeOffset(
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
                m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0,
                TimeSpan.FromHours(8));
            return dt.ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    static double? MsToEpoch(JsonElement? el)
    {
        var x = F(el);
        if (x is null or <= 0) return null;
        return x > 1e11 ? x / 1000.0 : x;
    }

    // ————————————————————— 地震：ICL —————————————————————

    public static List<QuakeEvent> ParseIclEvents(JsonElement root)
    {
        var list = new List<QuakeEvent>();
        var items = new List<JsonElement>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            var data = Prop(root, "data");
            if (data is { ValueKind: JsonValueKind.Array }) items.AddRange(data.Value.EnumerateArray());
            else if (data is { ValueKind: JsonValueKind.Object }) items.Add(data.Value);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(root.EnumerateArray());
        }

        foreach (var it in items)
        {
            var eid = S(Prop(it, "eventId", "event_id"));
            var lat = F(Prop(it, "latitude"));
            var lon = F(Prop(it, "longitude"));
            var mag = F(Prop(it, "magnitude"));
            var origin = MsToEpoch(Prop(it, "startAt"));
            if (eid.Length == 0 || lat is null || lon is null || mag is null || origin is null) continue;

            var counties = new List<CountyInfo>();
            if (Prop(it, "counties") is { ValueKind: JsonValueKind.Array } cs)
                counties = ParseCounties(cs);

            list.Add(new QuakeEvent(
                Uid: "icl:" + eid,
                Source: "icl",
                EventId: eid,
                Revision: (int)(F(Prop(it, "updates")) ?? 1),
                OriginTs: origin.Value,
                UpdatedTs: MsToEpoch(Prop(it, "updateAt")) ?? origin.Value,
                Lat: lat.Value,
                Lon: lon.Value,
                Depth: F(Prop(it, "depth")) ?? 0,
                Magnitude: mag.Value,
                Epicenter: S(Prop(it, "epicenter"), "未知震中"),
                MaxIntensity: F(Prop(it, "epiIntensity")),
                Counties: counties.Count > 0 ? counties : null));
        }
        return list;
    }

    /// <summary>ICL 详情里的 counties（逐县距离/烈度/倒计时）。</summary>
    public static List<CountyInfo> ParseIclDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new List<CountyInfo>();
        var data = Prop(root, "data");
        if (data is { ValueKind: JsonValueKind.Array } arr)
        {
            var first = arr.EnumerateArray().FirstOrDefault();
            data = first.ValueKind == JsonValueKind.Object ? first : null;
        }
        if (data is not { ValueKind: JsonValueKind.Object }) return new List<CountyInfo>();
        return Prop(data.Value, "counties") is { ValueKind: JsonValueKind.Array } cs
            ? ParseCounties(cs)
            : new List<CountyInfo>();
    }

    static List<CountyInfo> ParseCounties(JsonElement array)
    {
        var rows = new List<CountyInfo>();
        foreach (var c in array.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var name = S(Prop(c, "name"));
            rows.Add(new CountyInfo(
                name,
                S(Prop(c, "full_name"), name),
                S(Prop(c, "city")),
                F(Prop(c, "distance")),
                F(Prop(c, "intensity")),
                F(Prop(c, "countdown"))));
        }
        return rows;
    }

    // ————————————————————— 地震：Wolfx —————————————————————

    public static List<QuakeEvent> ParseWolfxEvents(JsonElement root, string sourceKey)
    {
        var items = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(root.EnumerateArray());
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "list", "data", "Data", "events" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
                {
                    items.AddRange(v.EnumerateArray());
                    break;
                }
            }
            if (items.Count == 0) items.Add(root);
        }

        var outv = new List<QuakeEvent>();
        foreach (var it in items)
        {
            if (it.ValueKind != JsonValueKind.Object) continue;

            var lat = F(Prop(it, "Latitude", "latitude", "lat"));
            var lon = F(Prop(it, "Longitude", "longitude", "lon"));
            var mag = F(Prop(it, "Magnitude", "Magunitude", "mag", "magnitude"));
            var origin = ParseBjTime(Prop(it, "OriginTime", "time", "ReportTime")) ?? MsToEpoch(Prop(it, "startAt"));
            if (lat is null || lon is null || mag is null || origin is null) continue;

            var eid = S(Prop(it, "EventID", "eventId", "ID", "id"));
            if (eid.Length == 0)
                eid = string.Create(CultureInfo.InvariantCulture, $"{lat.Value:F3},{lon.Value:F3},{(int)origin.Value}");

            var reports = (int)(F(Prop(it, "ReportNum")) ?? 0);

            outv.Add(new QuakeEvent(
                Uid: sourceKey + ":" + eid,
                Source: sourceKey,
                EventId: eid,
                Revision: (int)(F(Prop(it, "ReportNum", "update")) ?? 1),
                OriginTs: origin.Value,
                UpdatedTs: ParseBjTime(Prop(it, "ReportTime")) ?? origin.Value,
                Lat: lat.Value,
                Lon: lon.Value,
                Depth: F(Prop(it, "Depth", "depth")) ?? 0,
                Magnitude: mag.Value,
                Epicenter: S(Prop(it, "HypoCenter", "location", "place"), "未知震中"),
                MaxIntensity: F(Prop(it, "MaxIntensity", "intensity")),
                ReportCount: reports > 0 ? reports : null));
        }
        return outv;
    }

    // ————————————————————— 气象：nmc.cn —————————————————————

    static readonly (string Word, string Level)[] LevelWords =
    {
        ("红色", QuakeLevels.Red), ("橙色", QuakeLevels.Orange),
        ("黄色", QuakeLevels.Yellow), ("蓝色", QuakeLevels.Blue),
    };

    [GeneratedRegex(@"发布(.+?)(红色|橙色|黄色|蓝色)预警信号")]
    private static partial Regex SignalPattern();

    public static List<WeatherEvent> ParseNmcAlerts(JsonElement root, string province, bool strict)
    {
        var result = new List<WeatherEvent>();
        if (root.ValueKind != JsonValueKind.Object) return result;

        var data = Prop(root, "data");
        if (data is not { ValueKind: JsonValueKind.Object }) return result;
        var page = Prop(data.Value, "page");
        if (page is not { ValueKind: JsonValueKind.Object }) return result;
        var items = Prop(page.Value, "list");
        if (items is not { ValueKind: JsonValueKind.Array }) return result;

        foreach (var it in items.Value.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;

            var aid = S(Prop(it, "alertid"));
            var title = S(Prop(it, "title"));
            if (aid.Length == 0 || title.Length == 0) continue;
            if (strict && province.Length > 0 && !title.StartsWith(province, StringComparison.Ordinal)) continue;

            var level = QuakeLevels.Blue;
            foreach (var (word, lv) in LevelWords)
            {
                if (title.Contains(word, StringComparison.Ordinal))
                {
                    level = lv;
                    break;
                }
            }

            var m = SignalPattern().Match(title);
            var signal = m.Success ? m.Groups[1].Value.Trim() : "";
            if (signal.Length == 0) signal = title;

            var county = "";
            var idx = title.IndexOf("发布", StringComparison.Ordinal);
            if (idx > 0)
            {
                county = title[..idx];
                if (province.Length > 0 && county.StartsWith(province, StringComparison.Ordinal))
                    county = county[province.Length..];
            }
            county = county.Replace("气象台", "").Replace("气象", "").Trim();

            result.Add(new WeatherEvent(
                Uid: "nmc:" + aid,
                AlertId: aid,
                IssuedTs: ParseBjTime(Prop(it, "issuetime")),
                Title: title,
                Signal: signal,
                Level: level,
                Province: province,
                County: county,
                Url: S(Prop(it, "url"))));
        }
        return result;
    }

    // ————————————————————— 去重合并 —————————————————————

    static bool SameQuake(QuakeEvent a, QuakeEvent b)
    {
        if (a.EventId.Length > 0 && a.EventId == b.EventId) return true;
        return Math.Abs(a.Lat - b.Lat) <= 0.5
               && Math.Abs(a.Lon - b.Lon) <= 0.5
               && Math.Abs(a.Magnitude - b.Magnitude) <= 0.5
               && Math.Abs(a.OriginTs - b.OriginTs) <= 60;
    }

    /// <summary>把新事件并进既有集合（uid / 近似判定去重，revision 大者胜）。</summary>
    public static (Dictionary<string, QuakeEvent> Merged, List<QuakeEvent> Changed) MergeQuakes(
        IReadOnlyDictionary<string, QuakeEvent> existing,
        IEnumerable<QuakeEvent> incoming,
        int maxItems = 8)
    {
        var merged = new Dictionary<string, QuakeEvent>(existing, StringComparer.Ordinal);
        var changed = new List<QuakeEvent>();

        foreach (var ev in incoming)
        {
            string? hitKey = merged.ContainsKey(ev.Uid)
                ? ev.Uid
                : merged.FirstOrDefault(kv => SameQuake(kv.Value, ev)).Key;

            if (hitKey is null)
            {
                merged[ev.Uid] = ev;
                changed.Add(ev);
                continue;
            }

            var old = merged[hitKey];
            var newer = ev.Revision > old.Revision
                        || (ev.Revision == old.Revision && ev.UpdatedTs > old.UpdatedTs);
            if (!newer) continue;

            // 跨源命中同一地震时保持 uid 不变，避免重复提醒
            var keep = ev with { Uid = hitKey, Counties = ev.Counties ?? old.Counties };
            merged[hitKey] = keep;
            changed.Add(keep);
        }

        if (merged.Count > maxItems)
        {
            merged = merged.OrderByDescending(kv => kv.Value.OriginTs)
                .Take(maxItems)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        return (merged, changed);
    }

    public static Dictionary<string, QuakeEvent> PruneQuakes(
        IReadOnlyDictionary<string, QuakeEvent> events, double now, double maxAge = 180)
    {
        return events.Where(kv => now - kv.Value.OriginTs <= maxAge)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    public static Dictionary<string, WeatherEvent> PruneWeather(
        IReadOnlyDictionary<string, WeatherEvent> events, double now, double maxAge = 86400)
    {
        return events.Where(kv => kv.Value.IssuedTs is null || now - kv.Value.IssuedTs.Value <= maxAge)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
