using System.Text.Json;

namespace ClassDailyLand2.App.Integrations.Disaster;

/// <summary>
/// 全国省 / 地级市 / 区县 的经纬度检索。
///
/// 数据是内嵌资源 <c>Resources/cities.json</c>（由 Python 版 <c>cities.py</c> 生成，
/// 来源和风天气开源城市列表 Apache-2.0，31 省 / 366 市 / 2817 区县）。
/// 首次使用时才解析，之后常驻内存（约 100 KB JSON → 几 MB 对象，可以接受）。
/// </summary>
public static class Cities
{
    public const string NoWeather = "（不获取气象）";

    static readonly string[] Suffixes = { "自治州", "自治县", "自治区", "地区", "盟", "市", "县", "区", "旗", "省" };

    static readonly Lazy<Model> Data = new(Load, isThreadSafe: true);

    sealed record District(string Name, double Lat, double Lon);
    sealed record CityNode(string Name, double Lat, double Lon, IReadOnlyList<District> Districts);
    sealed record Province(string Name, IReadOnlyList<CityNode> Cities);

    sealed class Model
    {
        public required List<Province> Provinces { get; init; }
        public required Dictionary<(string Province, string City), List<CityNode>> CityIndex { get; init; }
        /// <summary>地名（全名与简称）→ (省, 市)；同名取先出现的。</summary>
        public required Dictionary<string, (string Province, string City)> Reverse { get; init; }
    }

    // ————————————————————— 加载 —————————————————————

    static Model Load()
    {
        var provinces = new List<Province>();
        var cityIndex = new Dictionary<(string, string), List<CityNode>>();
        var reverse = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        try
        {
            var asm = typeof(Cities).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("cities.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return new Model { Provinces = provinces, CityIndex = cityIndex, Reverse = reverse };

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return new Model { Provinces = provinces, CityIndex = cityIndex, Reverse = reverse };

            using var doc = JsonDocument.Parse(stream);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var pname = Str(p, "name");
                if (pname.Length == 0) continue;

                var cities = new List<CityNode>();
                if (p.TryGetProperty("cities", out var cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cs.EnumerateArray())
                    {
                        var cname = Str(c, "name");
                        if (cname.Length == 0) continue;

                        var districts = new List<District>();
                        if (c.TryGetProperty("districts", out var ds) && ds.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var d in ds.EnumerateArray())
                            {
                                if (d.ValueKind != JsonValueKind.Array || d.GetArrayLength() < 3) continue;
                                var dname = d[0].GetString() ?? "";
                                if (dname.Length == 0) continue;
                                districts.Add(new District(dname, d[1].GetDouble(), d[2].GetDouble()));
                            }
                        }

                        cities.Add(new CityNode(cname, Num(c, "lat"), Num(c, "lon"), districts));
                        Reg(reverse, cname, (pname, cname));
                        Reg(reverse, ShortName(cname), (pname, cname));
                        foreach (var d in districts)
                        {
                            Reg(reverse, d.Name, (pname, cname));
                            Reg(reverse, ShortName(d.Name), (pname, cname));
                        }
                    }
                }

                provinces.Add(new Province(pname, cities));
                cityIndex[(pname, "")] = cities.ToList();
            }
        }
        catch
        {
            // 资源缺失/损坏时退化为"没有城市表"：坐标靠设置里手填，功能不崩
        }

        return new Model { Provinces = provinces, CityIndex = cityIndex, Reverse = reverse };
    }

    static void Reg(Dictionary<string, (string, string)> map, string key, (string, string) value)
    {
        if (!string.IsNullOrEmpty(key)) map.TryAdd(key, value);
    }

    static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    static double Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ————————————————————— 查询 —————————————————————

    /// <summary>去掉行政区后缀：成都市→成都、甘孜藏族自治州→甘孜藏族。</summary>
    public static string ShortName(string? name)
    {
        var n = (name ?? "").Trim();
        foreach (var suf in Suffixes)
            if (n.EndsWith(suf, StringComparison.Ordinal) && n.Length > suf.Length)
                return n[..^suf.Length];
        return n;
    }

    /// <summary>全部省名（含"（不获取气象）"之外的 31 个）。</summary>
    public static IReadOnlyList<string> Provinces =>
        Data.Value.Provinces.Select(p => p.Name).ToList();

    /// <summary>某省的地级行政区名称列表。</summary>
    public static IReadOnlyList<string> CitiesOf(string? province)
    {
        var p = FindProvince(province);
        return p is null ? Array.Empty<string>() : p.Cities.Select(c => c.Name).ToList();
    }

    /// <summary>某市下辖区县名称列表。</summary>
    public static IReadOnlyList<string> DistrictsOf(string? province, string? city)
    {
        var p = FindProvince(province);
        if (p is null || string.IsNullOrWhiteSpace(city)) return Array.Empty<string>();
        var c = MatchKey(p.Cities, city);
        return c?.Districts.Select(d => d.Name).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>按 省/市/区县 取坐标；找不到返回 null。</summary>
    public static (double Lat, double Lon)? CoordOf(string? province, string? city, string district = "")
    {
        var prov = FindProvince(province);
        if (prov is not null && !string.IsNullOrWhiteSpace(city))
        {
            var cname = MatchKey(prov.Cities, city);
            if (cname is not null)
            {
                if (!string.IsNullOrWhiteSpace(district))
                {
                    var hit = MatchDistrict(cname.Districts, district);
                    if (hit is not null) return (hit.Lat, hit.Lon);
                }
                return (cname.Lat, cname.Lon);
            }
        }

        // 退化为按名字全局查
        if (!string.IsNullOrWhiteSpace(city) && ProvinceOfCity(city) is var (rp, rc) && rp.Length > 0)
            return CoordOf(rp, rc, "");

        return null;
    }

    /// <summary>按市/区县名反查 (省, 市)，找不到返回 ("","")。</summary>
    public static (string Province, string City) ProvinceOfCity(string? city)
    {
        var q = (city ?? "").Trim();
        if (q.Length == 0) return ("", "");
        if (Data.Value.Reverse.TryGetValue(q, out var hit)) return hit;
        if (Data.Value.Reverse.TryGetValue(ShortName(q), out var hit2)) return hit2;
        return ("", "");
    }

    /// <summary>
    ///把半截名字补成表里的标准写法（"四川"/"成都" → "四川省"/"成都市"）。
    ///
    /// <para>★ **必须带上省一起判断**，不能只拿市名反查：城市表里存在跨省重名 ——
    /// 比如湖南益阳有个「<b>资阳区</b>」，而四川有个「<b>资阳市</b>」，
    /// 反查表按省份顺序先注册先赢，只查市名会把"资阳"翻成"湖南省/益阳市"。
    /// 这里先按给定的省锁定范围，锁不住才退到全局反查。</para>
    /// </summary>
    public static (string Province, string City) Canonicalize(string? province, string? city)
    {
        var prov = FindProvince(province);
        if (prov is not null)
        {
            var match = MatchKey(prov.Cities, city);
            if (match is not null) return (prov.Name, match.Name);
            // 省认到了、市没认到：至少把省名补全
            return (prov.Name, (city ?? "").Trim());
        }

        // 省名没有或认不出 → 只能按市名全局反查
        var (p, c) = ProvinceOfCity(city);
        if (c.Length > 0) return (p, c);

        return ((province ?? "").Trim(), (city ?? "").Trim());
    }

    static Province? FindProvince(string? name)
    {
        var q = (name ?? "").Trim();
        if (q.Length == 0) return null;

        var exact = Data.Value.Provinces.FirstOrDefault(p => p.Name == q);
        if (exact is not null) return exact;

        return Data.Value.Provinces.FirstOrDefault(p =>
            p.Name.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(p.Name[..2], StringComparison.Ordinal));
    }

    static CityNode? MatchKey(IReadOnlyList<CityNode> items, string? name)
    {
        var q = (name ?? "").Trim();
        if (q.Length == 0) return null;
        var qs = ShortName(q);

        foreach (var c in items) if (c.Name == q) return c;
        foreach (var c in items) if (ShortName(c.Name) == qs) return c;
        foreach (var c in items)
        {
            var sn = ShortName(c.Name);
            if (c.Name.StartsWith(qs, StringComparison.Ordinal) ||
                qs.StartsWith(sn, StringComparison.Ordinal) ||
                sn.StartsWith(qs, StringComparison.Ordinal))
                return c;
        }
        return null;
    }

    static District? MatchDistrict(IReadOnlyList<District> items, string? name)
    {
        var q = (name ?? "").Trim();
        if (q.Length == 0) return null;
        var qs = ShortName(q);

        foreach (var d in items) if (d.Name == q) return d;
        foreach (var d in items) if (ShortName(d.Name) == qs) return d;
        foreach (var d in items)
        {
            var sn = ShortName(d.Name);
            if (d.Name.StartsWith(qs, StringComparison.Ordinal) ||
                qs.StartsWith(sn, StringComparison.Ordinal) ||
                sn.StartsWith(qs, StringComparison.Ordinal))
                return d;
        }
        return null;
    }

    // ————————————————————— 坐标反查 —————————————————————

    /// <summary>
    /// 按经纬度找**最近的区县**（IP 定位只给坐标时，用它反推省/市/区县）。
    ///
    /// <para>2817 条区县全遍历一遍，微秒级；只在内存里算，不联网。
    /// 没有下辖区县的市用市本身兜底，所以只要坐标落在国内就一定有结果。</para>
    /// </summary>
    public static (string Province, string City, string District, double Lat, double Lon, double DistanceKm)?
        NearestDistrict(double lat, double lon)
    {
        (string Province, string City, string District, double Lat, double Lon, double DistanceKm) best
            = ("", "", "", 0, 0, double.MaxValue);

        foreach (var p in Data.Value.Provinces)
        foreach (var c in p.Cities)
        {
            if (c.Districts.Count == 0)
            {
                var dc = QuakeCalc.HaversineKm(lat, lon, c.Lat, c.Lon);
                if (dc < best.DistanceKm) best = (p.Name, c.Name, "", c.Lat, c.Lon, dc);
                continue;
            }

            foreach (var d in c.Districts)
            {
                var dd = QuakeCalc.HaversineKm(lat, lon, d.Lat, d.Lon);
                if (dd < best.DistanceKm) best = (p.Name, c.Name, d.Name, d.Lat, d.Lon, dd);
            }
        }

        return best.DistanceKm == double.MaxValue ? null : best;
    }

    /// <summary>
    /// 按经纬度找最近的地级市（区县太远时的退路：至少把市认对，坐标取市中心）。
    /// </summary>
    public static (string Province, string City, double Lat, double Lon, double DistanceKm)?
        NearestCity(double lat, double lon)
    {
        (string Province, string City, double Lat, double Lon, double DistanceKm) best
            = ("", "", 0, 0, double.MaxValue);

        foreach (var p in Data.Value.Provinces)
        foreach (var c in p.Cities)
        {
            var d = QuakeCalc.HaversineKm(lat, lon, c.Lat, c.Lon);
            if (d < best.DistanceKm) best = (p.Name, c.Name, c.Lat, c.Lon, d);
        }

        return best.DistanceKm == double.MaxValue ? null : best;
    }
}
