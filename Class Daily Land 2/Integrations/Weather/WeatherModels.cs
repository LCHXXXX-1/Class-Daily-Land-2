namespace ClassDailyLand2.App.Integrations.Weather;

/// <summary>
/// 天气动画的种类常量。<see cref="WeatherArt"/> 控件只认这几个字符串，
/// 认不出的一律画成 <see cref="Cloudy"/> —— 这样新增数据源时不会画出空白。
/// </summary>
public static class WeatherArtKinds
{
    public const string Clear = "clear";                 // 晴（白天太阳 / 夜晚月亮，由 IsDay 决定）
    public const string Partly = "partly";               // 晴间多云 / 多云
    public const string Cloudy = "cloudy";               // 阴
    public const string Fog = "fog";                     // 雾 / 雾凇
    public const string Drizzle = "drizzle";             // 毛毛雨
    public const string Rain = "rain";                   // 小 / 中 / 大雨
    public const string Sleet = "sleet";                 // 冻雨 / 冻毛毛雨
    public const string Snow = "snow";                   // 小 / 中 / 大雪、阵雪
    public const string Showers = "showers";             // 阵雨
    public const string Thunder = "thunder";             // 雷阵雨
    public const string Hail = "hail";                   // 雷暴伴冰雹

    /// <summary>全部种类（设置页的动画预览按这个顺序铺开）。</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> All = new[]
    {
        (Clear, "晴"), (Partly, "多云"), (Cloudy, "阴"), (Fog, "雾"),
        (Drizzle, "毛毛雨"), (Rain, "雨"), (Showers, "阵雨"), (Sleet, "冻雨"),
        (Snow, "雪"), (Thunder, "雷阵雨"), (Hail, "冰雹"),
    };
}

/// <summary>一条天气码的中文文案与对应动画种类。</summary>
public sealed record WeatherDesc(string Text, string Art);

/// <summary>
/// WMO 天气码（Open-Meteo 用的那套 0~99）→ 中文文案 + 动画种类。
/// 纯映射、无 IO，可离线单测。
/// </summary>
public static class WeatherCodes
{
    /// <summary>
    /// 查表。<paramref name="isDay"/> 只影响晴天的文案（晴 / 晴夜），动画由控件自己按它选太阳或月亮。
    /// 认不出的码按「阴」处理 —— 宁可画得保守，也不要开天窗。
    /// </summary>
    public static WeatherDesc Describe(int code, bool isDay) => code switch
    {
        0 => new(isDay ? "晴" : "晴夜", WeatherArtKinds.Clear),
        1 => new(isDay ? "晴间多云" : "多云", WeatherArtKinds.Partly),
        2 => new("多云", WeatherArtKinds.Partly),
        3 => new("阴", WeatherArtKinds.Cloudy),
        45 or 48 => new(code == 48 ? "雾凇" : "雾", WeatherArtKinds.Fog),

        51 => new("轻毛毛雨", WeatherArtKinds.Drizzle),
        53 => new("毛毛雨", WeatherArtKinds.Drizzle),
        55 => new("密毛毛雨", WeatherArtKinds.Drizzle),
        56 or 57 => new("冻毛毛雨", WeatherArtKinds.Sleet),

        61 => new("小雨", WeatherArtKinds.Rain),
        63 => new("中雨", WeatherArtKinds.Rain),
        65 => new("大雨", WeatherArtKinds.Rain),
        66 or 67 => new("冻雨", WeatherArtKinds.Sleet),

        71 => new("小雪", WeatherArtKinds.Snow),
        73 => new("中雪", WeatherArtKinds.Snow),
        75 => new("大雪", WeatherArtKinds.Snow),
        77 => new("雪粒", WeatherArtKinds.Snow),

        80 => new("阵雨", WeatherArtKinds.Showers),
        81 => new("阵雨", WeatherArtKinds.Showers),
        82 => new("强阵雨", WeatherArtKinds.Showers),
        85 => new("阵雪", WeatherArtKinds.Snow),
        86 => new("强阵雪", WeatherArtKinds.Snow),

        95 => new("雷阵雨", WeatherArtKinds.Thunder),
        96 or 99 => new("雷暴伴冰雹", WeatherArtKinds.Hail),

        _ => new("阴", WeatherArtKinds.Cloudy),
    };

    /// <summary>风向角（0=北）→ 中文风向。</summary>
    public static string WindDirection(double degrees)
    {
        string[] names = { "北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风" };
        var i = (int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8;
        return names[i];
    }

    /// <summary>km/h → 级（蒲福风级近似），用于「东南风 3 级」这种文案。</summary>
    public static int WindScale(double kmh) => kmh switch
    {
        < 1 => 0,
        < 6 => 1,
        < 12 => 2,
        < 20 => 3,
        < 29 => 4,
        < 39 => 5,
        < 50 => 6,
        < 62 => 7,
        < 75 => 8,
        < 89 => 9,
        < 103 => 10,
        < 118 => 11,
        _ => 12,
    };
}

/// <summary>
/// 一次定位的结果。<see cref="Source"/> 必须写清楚坐标是哪来的 ——
/// IP 定位是市级精度，地震倒计时会跟着偏，状态卡片要能一眼看出用的是哪套坐标。
/// </summary>
/// <param name="Source">"ip-api" / "ipapi.co" / "pconline" / "manual" / "city-table"</param>
public sealed record WeatherLocation(
    double Lat,
    double Lon,
    string Province,
    string City,
    string District,
    string Source);

/// <summary>一次天气查询的结果（内存快照，重启靠 weather-cache.json 恢复）。</summary>
public sealed record WeatherSnapshot(
    double Temp,
    double Feels,
    double Humidity,
    double WindSpeedKmh,
    double WindDegrees,
    int Code,
    bool IsDay,
    string Text,
    string Art,
    double Lat,
    double Lon,
    string Place,
    string Source,
    double FetchedTs)
{
    /// <summary>「24°」——副岛展开态与主窗徽标用。</summary>
    public string TempText => $"{Math.Round(Temp):0}°";

    /// <summary>主窗区块的两行正文。</summary>
    public IReadOnlyList<string> Lines => new[]
    {
        $"{TempText} {Text} · 体感 {Math.Round(Feels):0}° · 湿度 {Humidity:0}%",
        $"{WeatherCodes.WindDirection(WindDegrees)} {WeatherCodes.WindScale(WindSpeedKmh)} 级 · {Place}",
    };
}
