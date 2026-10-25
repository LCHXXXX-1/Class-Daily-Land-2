using System.Globalization;

namespace ClassDailyLand.App.Integrations.Disaster;

/// <summary>烈度等级色键与中文名（与 Python 版 <c>quake_calc</c> 一致）。</summary>
public static class QuakeLevels
{
    public const string Blue = "blue";
    public const string Yellow = "yellow";
    public const string Orange = "orange";
    public const string Red = "red";

    public static readonly string[] Order = { Blue, Yellow, Orange, Red };

    public static readonly IReadOnlyDictionary<string, string> Colors =
        new Dictionary<string, string>
        {
            [Blue] = "#4da3ff",
            [Yellow] = "#ffd23f",
            [Orange] = "#ff9f0a",
            [Red] = "#ff453a",
        };

    public static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string> { [Blue] = "蓝", [Yellow] = "黄", [Orange] = "橙", [Red] = "红" };

    public static readonly IReadOnlyDictionary<string, string> Words =
        new Dictionary<string, string> { [Blue] = "蓝色", [Yellow] = "黄色", [Orange] = "橙色", [Red] = "红色" };

    public static readonly IReadOnlyDictionary<string, int> Rank =
        new Dictionary<string, int> { [Blue] = 0, [Yellow] = 1, [Orange] = 2, [Red] = 3 };
}

/// <summary>
/// 地震预警核心算法：Haversine 距离、本地烈度、横波到达倒计时。
/// 逐行移植自 Python 版 <c>builtin/disaster/quake_calc.py</c>
/// （其本身移植自参考项目 HuaniaEarthquakeCalculator）。
///
/// 纯函数、无 IO，可离线单测。
/// </summary>
public static class QuakeCalc
{
    public const double EarthRadiusKm = 6378.137;

    /// <summary>只对 120 秒内到达的事件高频展示。</summary>
    public const double MaxLeadSec = 120;

    /// <summary>发震超过 3 分钟不再评估。</summary>
    public const double MaxAgeSec = 180;

    /// <summary>横波已到达后继续显示多久。</summary>
    public const double ArrivedHoldSec = 60;

    public static readonly double[] DefaultThresholds = { 2.0, 3.0, 5.0 };

    // ————————————————————— 基础量 —————————————————————

    /// <summary>两点球面距离（km），保留 4 位小数（与参考实现一致）。</summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double p = Math.PI / 180.0;
        var a = Math.Pow(Math.Sin((lat1 - lat2) * p / 2.0), 2)
                + Math.Cos(lat1 * p) * Math.Cos(lat2 * p) * Math.Pow(Math.Sin((lon1 - lon2) * p / 2.0), 2);
        a = Math.Clamp(a, 0, 1);
        return Math.Round(2 * Math.Asin(Math.Sqrt(a)) * EarthRadiusKm, 4);
    }

    /// <summary>本地烈度估算，负值取 0。</summary>
    public static double IntensityAt(double magnitude, double distanceKm)
    {
        if (double.IsNaN(magnitude) || double.IsNaN(distanceKm)) return 0;
        var v = magnitude * 1.363 + 2.941 - Math.Log(distanceKm + 7.0) * 1.494;
        return v < 0 ? 0 : v;
    }

    /// <summary>横波到达倒计时（秒）：按震源深度分档查表 + 线性插值。</summary>
    public static double CountdownSeconds(double depthKm, double distanceKm)
    {
        if (depthKm < 0 || distanceKm < 0) return 0;

        var i = (int)(depthKm / 5.0);
        if (i > 6) i = 6;

        var axis = QuakeTables.Table[0];
        var row = QuakeTables.Table[i + 1];

        if (distanceKm > axis[^1])
            return QuakeTables.Slope[i] * distanceKm + QuakeTables.Intercept[i];

        var n = axis.Length - 1;
        var j = 0;
        while (j < n && distanceKm >= axis[j]) j++;
        var i3 = j - 1;
        var i4 = i3 + 1;
        return row[i3] + (row[i4] - row[i3]) * ((distanceKm - axis[i3]) / (axis[i4] - axis[i3]));
    }

    // ————————————————————— 等级 —————————————————————

    /// <summary>把用户配置的三个分界值整理成递增数组，非法时回退默认。</summary>
    public static double[] NormThresholds(IReadOnlyList<double>? thresholds)
    {
        var outv = new double[DefaultThresholds.Length];
        for (var i = 0; i < outv.Length; i++)
        {
            var val = DefaultThresholds[i];
            if (thresholds is not null && i < thresholds.Count)
            {
                var t = thresholds[i];
                if (!double.IsNaN(t) && t >= 0 && t <= 12) val = t;
            }
            outv[i] = val;
        }
        Array.Sort(outv);
        return outv;
    }

    public static string LevelOf(double intensity, IReadOnlyList<double>? thresholds = null)
    {
        if (double.IsNaN(intensity)) return QuakeLevels.Blue;
        var t = NormThresholds(thresholds);
        if (intensity <= t[0]) return QuakeLevels.Blue;
        if (intensity <= t[1]) return QuakeLevels.Yellow;
        if (intensity <= t[2]) return QuakeLevels.Orange;
        return QuakeLevels.Red;
    }

    public static bool LevelAtLeast(string? level, string? minimum) =>
        Rank(level) >= Rank(minimum);

    public static int Rank(string? level) =>
        level is not null && QuakeLevels.Rank.TryGetValue(level, out var r) ? r : 0;

    public static string LevelName(string? level) =>
        level is not null && QuakeLevels.Names.TryGetValue(level, out var n) ? n : "";

    /// <summary>完整等级词（"橙色预警" 而非 "橙预警"）。</summary>
    public static string LevelWord(string? level) =>
        level is not null && QuakeLevels.Words.TryGetValue(level, out var n) ? n : "";

    public static string LevelColor(string? level, IReadOnlyDictionary<string, string>? custom = null)
    {
        if (custom is not null && level is not null &&
            custom.TryGetValue(level, out var c) && !string.IsNullOrWhiteSpace(c))
            return c.Trim();
        return level is not null && QuakeLevels.Colors.TryGetValue(level, out var d) ? d : QuakeLevels.Colors[QuakeLevels.Blue];
    }

    // ————————————————————— 时间 —————————————————————

    /// <summary>epoch 秒 → 北京时间 HH:MM:SS（固定 UTC+8，无夏令时）。</summary>
    public static string FmtClock(double? ts)
    {
        if (ts is null || double.IsNaN(ts.Value)) return "--:--:--";
        var t = DateTimeOffset.FromUnixTimeSeconds((long)ts.Value).ToOffset(TimeSpan.FromHours(8));
        return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FmtClockHm(double? ts) => FmtClock(ts) is var s && s.Length >= 5 ? s[..5] : s;

    // ————————————————————— 评估 —————————————————————

    /// <summary>
    /// 把一条地震事件换算成本地视角的派生数据。
    /// 坐标/字段不可用时返回 null。
    /// </summary>
    public static QuakeEvaluation? Evaluate(
        QuakeEvent ev,
        double? lat,
        double? lon,
        double now,
        double threshold = 2.0,
        IReadOnlyList<double>? thresholds = null)
    {
        if (lat is null || lon is null) return null;

        var dist = HaversineKm(lat.Value, lon.Value, ev.Lat, ev.Lon);
        var inten = IntensityAt(ev.Magnitude, dist);
        var cd = CountdownSeconds(ev.Depth, dist);
        var arrive = ev.OriginTs + cd;
        var remain = arrive - now;
        var age = now - ev.OriginTs;

        var phase = remain > 0 ? "incoming"
            : now <= arrive + ArrivedHoldSec ? "arrived"
            : "past";

        var triggered = inten > threshold
                        && phase == "incoming"
                        && remain <= MaxLeadSec
                        && age <= MaxAgeSec
                        && ev.OriginTs <= now + 30;

        var displayable = inten > threshold
                          && (phase == "incoming" || phase == "arrived")
                          && age <= MaxAgeSec + ArrivedHoldSec;

        return new QuakeEvaluation(
            Uid: ev.Uid,
            DistanceKm: dist,
            LocalIntensity: Math.Round(inten, 2),
            Level: LevelOf(inten, thresholds),
            CountdownSec: cd,
            ArriveTs: arrive,
            RemainSec: remain,
            Phase: phase,
            Triggered: triggered,
            Displayable: displayable,
            Event: ev);
    }
}
