using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClassDailyLand2.App.Integrations.Weather;

/// <summary>
/// 实时天气数据源：Open-Meteo（免 key、无需注册、可商用）。
///
/// <para>★ 国内可达性需实机验证 —— 离线无法确认。失败时服务层会保留上一次的快照，
/// 并在面板与状态卡片上标出「多久没更新」，不会把旧数据当成新的糊弄人。</para>
/// </summary>
public static class WeatherClient
{
    /// <summary>
    /// 构造查询串。温度/湿度/体感/天气码/风速风向 + <c>is_day</c>（区分晴天画太阳还是月亮）。
    /// 坐标必须用不变文化格式化 —— 逗号小数分隔符会让 URL 直接 400。
    /// </summary>
    public static string ForecastUrl(double lat, double lon) =>
        "https://api.open-meteo.com/v1/forecast" +
        "?latitude=" + lat.ToString("0.####", CultureInfo.InvariantCulture) +
        "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture) +
        "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code" +
        ",wind_speed_10m,wind_direction_10m" +
        "&wind_speed_unit=kmh&timezone=auto";

    /// <summary>解析当前实况。字段缺失时返回 null（宁可没有也不给个 0℃ 的假数据）。</summary>
    public static WeatherSnapshot? Parse(JsonElement root, WeatherLocation loc, double nowTs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object) return null;

        var temp = Num(cur, "temperature_2m");
        var code = Num(cur, "weather_code");
        if (double.IsNaN(temp) || double.IsNaN(code)) return null;

        var feels = Num(cur, "apparent_temperature");
        var humidity = Num(cur, "relative_humidity_2m");
        var wind = Num(cur, "wind_speed_10m");
        var dir = Num(cur, "wind_direction_10m");
        // ★ NaN >= 0.5 恒为 false：缺字段时原来会判成"夜里"，与注释意图正好相反
        //   （界面上大白天挂一轮月亮）。NaN 单独放行成白天。
        var isDayRaw = Num(cur, "is_day");
        var isDay = double.IsNaN(isDayRaw) || isDayRaw >= 0.5;

        var desc = WeatherCodes.Describe((int)code, isDay);

        return new WeatherSnapshot(
            Temp: temp,
            Feels: double.IsNaN(feels) ? temp : feels,
            Humidity: double.IsNaN(humidity) ? 0 : humidity,
            WindSpeedKmh: double.IsNaN(wind) ? 0 : wind,
            WindDegrees: double.IsNaN(dir) ? 0 : dir,
            Code: (int)code,
            IsDay: isDay,
            Text: desc.Text,
            Art: desc.Art,
            Lat: loc.Lat,
            Lon: loc.Lon,
            Place: PlaceOf(loc),
            Source: loc.Source,
            FetchedTs: nowTs);
    }

    /// <summary>副岛/主窗上那行地名：市 + 区县，缺市退省，再缺就只能报坐标。</summary>
    static string PlaceOf(WeatherLocation loc)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(loc.City)) parts.Add(loc.City);
        if (!string.IsNullOrWhiteSpace(loc.District)) parts.Add(loc.District);
        if (parts.Count > 0) return string.Join(" · ", parts);

        if (!string.IsNullOrWhiteSpace(loc.Province)) return loc.Province;
        return $"{loc.Lat:0.##}, {loc.Lon:0.##}";
    }

    static double Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) &&
        !double.IsNaN(d) && !double.IsInfinity(d) ? d : double.NaN;
}

/// <summary>天气模块自己的 HTTP（10 秒超时 + GBK 兜底 + 人话错误）。</summary>
/// <remarks>
/// 与 <c>DisasterSources.FetchJsonAsync</c> 是同款实现的**有意重复**：
/// 两边 User-Agent 与超时策略将来会分头演进，而把灾害那条链路的 HTTP 抽去共享
/// 要动 <c>DisasterSources</c> 这条已经上线的代码，风险不划算。
/// </remarks>
public static class WeatherHttp
{
    const string UserAgent = "ClassDailyLand2-Weather/1.0";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,                 // 走系统代理（校园网 / 公司网常见）
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.8");
        return client;
    }

    public static async Task<(JsonDocument? Doc, string Error)> FetchJsonAsync(
        string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 个别源对 UA / Referer 有要求（ipapi.co 的文档就明写要带 UA），按源单独加
            if (headers is not null)
                foreach (var kv in headers)
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");

            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            if (bytes.Length == 0) return (null, "空响应");

            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding("GB18030").GetString(bytes);   // pconline 回 GBK
            }

            if (string.IsNullOrWhiteSpace(text)) return (null, "空响应");
            return (JsonDocument.Parse(text), "");
        }
        catch (TaskCanceledException) { return (null, "请求超时"); }
        catch (OperationCanceledException) { return (null, "请求已取消"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { return (null, ex.Message); }
    }
}
