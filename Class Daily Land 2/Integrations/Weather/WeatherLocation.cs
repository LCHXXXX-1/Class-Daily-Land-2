using ClassDailyLand2.App.Integrations.Disaster;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ClassDailyLand2.App.Integrations.Weather;

/// <summary>
/// 天气/预警的**自动定位**：IP → 省市 → 内嵌城市表坐标。
///
/// <para>★ 三个源都是免 key 的，但**国内可达性必须实机验证**（离线写代码时无法确认），
/// 所以做成按顺序试的链路，每个源失败都记一行日志，最后一个都不通就交给调用方回退到
/// 「手选省市区查表」。顺序按「国内优先 + 中文名能对上城市表」排：</para>
/// <list type="number">
///   <item>太平洋电脑网 whois：国内、返回中文省市区、**没有经纬度** → 拿名字查本程序内嵌的
///         <c>cities.json</c>。坐标口径与用户手选省市区完全一致，对地震倒计时反而更稳。</item>
///   <item>ip-api.com：直接给经纬度，但免费档只走 http，且省市区是中文。</item>
///   <item>ipapi.co：https，省市区是英文（对不上城市表），只有经纬度可用。</item>
/// </list>
///
/// <para>坐标精度是**市级**（IP 归属地可能给到运营商机房）。拿它算地震倒计时会偏，
/// 所以状态卡片必须一直标出 <see cref="WeatherLocation.Source"/>，用户可一键关掉自动定位。</para>
/// </summary>
public static class WeatherLocator
{
    /// <summary>结果缓存刷新间隔：换城市才需要重查，没必要每次启动都打外网。</summary>
    public const double CacheTtlSec = 24 * 3600;

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    sealed record CacheDto(
        double Lat, double Lon, string Province, string City, string District,
        string Source, double FetchedTs);

    // ————————————————————— 对外 —————————————————————

    /// <summary>读缓存（24 小时内有效）。没有或过期返回 null。</summary>
    public static WeatherLocation? LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dto = System.Text.Json.JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(path));
            if (dto is null) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - dto.FetchedTs > CacheTtlSec) return null;
            if (double.IsNaN(dto.Lat) || double.IsNaN(dto.Lon)) return null;
            return new WeatherLocation(dto.Lat, dto.Lon, dto.Province, dto.City, dto.District, dto.Source);
        }
        catch { return null; }   // 缓存坏了就当没有，绝不影响启动
    }

    public static void SaveCache(string path, WeatherLocation loc)
    {
        try
        {
            var dto = new CacheDto(loc.Lat, loc.Lon, loc.Province, loc.City, loc.District,
                loc.Source, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Core.Storage.AtomicJson.Write(path, dto);
        }
        catch { /* 写不进去下次再查一遍而已 */ }
    }

    /// <summary>
    /// **并行**问所有 IP 源，取信息最全的那一个（区县 &gt; 市 &gt; 省）。
    ///
    /// <para>串行逐个试有个致命问题：先成功的那个如果只回了省名，后面能给出区县的源就永远轮不到 ——
    /// 用户看到的结果就是"只有四川省、没有市"。四个源都是免 key 的小请求、一天最多一次，
    /// 并行的成本可以忽略，换来的是"只要有一个源能给出区县，就能拿到区县"。</para>
    /// </summary>
    public static async Task<(WeatherLocation? Loc, string Error)> LocateAsync(Action<string>? onError = null)
    {
        var probes = Sources.Select(s => ProbeAsync(s.Name, s.Url, s.Headers)).ToArray();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        WeatherLocation? best = null;
        var bestScore = 0;
        var errors = new List<string>();

        // Sources 的顺序就是同分时的优先级（靠前的优先）
        for (var i = 0; i < results.Length; i++)
        {
            var (loc, error, seenIp) = results[i];
            if (loc is null)
            {
                errors.Add(error);
                onError?.Invoke($"[天气定位] {error}");
                continue;
            }

            var score = Score(Sources[i].Name, loc);
            onError?.Invoke($"[天气定位] {Sources[i].Name} → IP {(seenIp.Length == 0 ? "?" : seenIp)} → " +
                            $"{loc.Province}/{loc.City}/{loc.District} {loc.Lat:0.##},{loc.Lon:0.##}（优先级 {score}）");
            if (score > bestScore)
            {
                bestScore = score;
                best = loc;
            }
        }

        return (best, best is null ? string.Join("；", errors) : "");
    }

    /// <summary>
    /// 打分：**国内的名称源优先，完整度只作同级细分**。
    ///
    /// <para>为什么不是"越全越好"：ip-api 给了坐标，我们就能反查出某个区，
    /// 于是它永远满分、永远赢 —— 哪怕它把出口 IP 认到了隔壁市。
    /// ipip / pconline 用的是国内 IP 库，对国内地址通常更准；
    /// 只要它们给了市，就该压过"坐标反查出来的区"。</para>
    /// </summary>
    static int Score(string source, WeatherLocation loc)
    {
        var fromCnNameSource = source is "ipip" or "pconline";
        var hasDistrict = !string.IsNullOrWhiteSpace(loc.District);
        var hasCity = !string.IsNullOrWhiteSpace(loc.City);

        if (fromCnNameSource && hasDistrict) return 40;
        if (fromCnNameSource && hasCity) return 30;
        if (hasDistrict) return 20;
        if (hasCity) return 10;
        return 1;
    }

    static async Task<(WeatherLocation? Loc, string Error, string SeenIp)> ProbeAsync(
        string name, string url, IReadOnlyDictionary<string, string>? headers)
    {
        var (doc, error) = await WeatherHttp.FetchJsonAsync(url, headers).ConfigureAwait(false);
        if (doc is null) return (null, $"{name}: {error}", "");

        using (doc)
        {
            // 把"服务看到的 IP"也带出来：它要是和你真实的出口 IP 不一样，
            // 说明这台机器走了代理/VPN —— 定位偏到别的城市基本就是这个原因。
            var ip = Str(doc.RootElement, "ip");
            if (ip.Length == 0 &&
                doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                ip = Str(data, "ip");

            var loc = Parse(name, doc.RootElement);
            return loc is null
                ? (null, $"{name}: 响应里没有可用的省市区或经纬度", ip)
                : (loc, "", ip);
        }
    }

    /// <summary>
    /// 回退：拿用户手选的 省/市/区 查内嵌城市表（与手动模式同一口径）。
    /// 只选了省没选市时退到省会 —— 否则「四川省」这种默认值拿不到坐标。
    /// </summary>
    public static WeatherLocation? FromCityTable(string province, string city, string district)
    {
        if (string.IsNullOrWhiteSpace(province)) return null;

        var (prov, cty) = Cities.Canonicalize(province, city);
        var coord = Cities.CoordOf(prov, cty, district);
        if (coord is null)
        {
            var capital = Cities.CitiesOf(prov).FirstOrDefault();
            if (capital is not null)
            {
                cty = capital;
                coord = Cities.CoordOf(prov, capital);
            }
        }
        if (coord is null) return null;

        return new WeatherLocation(coord.Value.Lat, coord.Value.Lon,
            prov, cty, district ?? "", "city-table");
    }

    // ————————————————————— 源与解析 —————————————————————

    static readonly (string Name, string Url, IReadOnlyDictionary<string, string>? Headers)[] Sources =
    {
        // 坐标源：能给经纬度，就能反查出区县
        // （ip-api 免费档只支持 http，https 会回 403 —— 这是它文档里写死的）
        ("ip-api", "http://ip-api.com/json/?lang=zh-CN&fields=status,message,country,regionName,city,lat,lon", null),
        ("ipapi.co", "https://ipapi.co/json/", new Dictionary<string, string>
        {
            // ipapi.co 文档要求带 UA，不带容易吃 403
            ["User-Agent"] = "ipapi.co/#c-sharp-v1.03",
        }),
        // 名称源：国内可达性通常更好，给的是中文省市；ipip 有时还能给到区县
        ("ipip", "https://myip.ipip.net/json", null),
        ("pconline", "http://whois.pconline.com.cn/ipJson.jsp?json=true", new Dictionary<string, string>
        {
            ["Referer"] = "http://whois.pconline.com.cn/",
        }),
    };

    /// <summary>按源解析一份响应（纯函数，可离线单测）。</summary>
    public static WeatherLocation? Parse(string source, JsonElement root)
    {
        switch (source)
        {
            case "ip-api":
                if (Str(root, "status") == "fail") return null;
                return FromCoords(Num(root, "lat"), Num(root, "lon"), source,
                    Str(root, "regionName"), Str(root, "city"));

            case "ipapi.co":
                return FromCoords(Num(root, "latitude"), Num(root, "longitude"), source,
                    Str(root, "region"), Str(root, "city"));

            case "ipip":
            {
                // {"ret":"ok","data":{"ip":"…","location":["中国","四川省","成都市","龙泉驿区","电信"]}}
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return null;
                if (!data.TryGetProperty("location", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;

                var parts = arr.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "").Trim() : "")
                    .ToList();
                string At(int i) => i < parts.Count ? parts[i] : "";

                return FromNames(At(1), At(2), At(3), source);
            }

            default:   // pconline：只有 pro / city，没有区县和经纬度
                return FromNames(Str(root, "pro"), Str(root, "city"), "", source);
        }
    }

    /// <summary>
    /// 有坐标时的解析：**用坐标反查最近的区县**，把省/市/区县一次补齐。
    ///
    /// <para>IP 坐标经常落在运营商机房或城市中心，所以只要最近的区县在 80km 内就采信它，
    /// 并把坐标换成区县中心 —— 对用户实际位置来说通常比 IP 原始点更接近。
    /// 80~200km 退到地级市；再远（境外 / 坐标异常）才保留 IP 原始坐标。</para>
    /// </summary>
    static WeatherLocation? FromCoords(double lat, double lon, string source, string provinceHint, string cityHint)
    {
        // Num 缺字段时回 NaN，而 NaN 与任何数比较都是 false —— 不先拦掉会一路通过范围判断
        if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return null;

        if (Cities.NearestDistrict(lat, lon) is { } d && d.DistanceKm <= 80)
            return new WeatherLocation(d.Lat, d.Lon, d.Province, d.City, d.District, source);

        if (Cities.NearestCity(lat, lon) is { } c && c.DistanceKm <= 200)
            return new WeatherLocation(c.Lat, c.Lon, c.Province, c.City, "", source);

        return new WeatherLocation(lat, lon, provinceHint, cityHint, "", source);
    }

    /// <summary>
    /// 只有名字时的解析：**先按「源给的省 + 市」一起查表**，锁不住才退到按市名反查。
    /// （"成都" 与 "成都市"、"甘孜州" 与 "甘孜藏族自治州" 都要能对上。）
    /// </summary>
    static WeatherLocation? FromNames(string province, string city, string district, string source)
    {
        if (string.IsNullOrWhiteSpace(province) && string.IsNullOrWhiteSpace(city)) return null;

        // ★ 绝不能只拿市名反查：湖南益阳有个「资阳区」、四川有个「资阳市」，
        //   反查表按省序先注册先赢 —— 只查市名会把"四川/资阳"翻成"湖南/益阳"，
        //   定位就跑到 1000 公里外的益阳去了（实测踩过）。
        var (prov, cty) = Cities.Canonicalize(province, city);

        var coord = Cities.CoordOf(prov, cty, district);
        if (coord is null) return null;

        // 区县名只有能对上表里某一个才保留 —— 免得显示一个根本不存在的区
        var keepDistrict = !string.IsNullOrWhiteSpace(district) &&
                           Cities.DistrictsOf(prov, cty)
                                 .Any(x => string.Equals(x, district, StringComparison.Ordinal));

        return new WeatherLocation(coord.Value.Lat, coord.Value.Lon,
            prov, cty, keepDistrict ? district : "", source);
    }

    static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    static double Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) &&
        !double.IsNaN(d) && !double.IsInfinity(d) ? d : double.NaN;
}
