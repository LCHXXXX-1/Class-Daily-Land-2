using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Integrations.Weather;
using ClassDailyLand2.Plugin;
using System.Text.Json;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 实时天气这条链路的**纯逻辑**部分：天气码映射、风向风级、Open-Meteo 解析、
/// 定位回退、动画种类规整，以及插件契约 <c>BoardSection</c> 的向后兼容。
///
/// 网络与 UI 一律不碰（与 <c>DisasterIntegrationTests</c> 同一套取向）。
/// </summary>
public sealed class WeatherTests
{
    // ————————————————————— 天气码 —————————————————————

    [Fact]
    public void WeatherCodes_EveryWmoCodeHasTextAndARealArt()
    {
        var kinds = WeatherArtKinds.All.Select(a => a.Kind).ToHashSet(StringComparer.Ordinal);

        for (var code = 0; code <= 99; code++)
        {
            var d = WeatherCodes.Describe(code, isDay: true);
            Assert.False(string.IsNullOrWhiteSpace(d.Text), $"code {code} 没有文案");
            Assert.Contains(d.Art, kinds);
        }
    }

    [Fact]
    public void WeatherCodes_UnknownCodeFallsBackToCloudy()
    {
        // 认不出的码按「阴」画 —— 宁可保守也不要开天窗
        var d = WeatherCodes.Describe(42, isDay: true);
        Assert.Equal(WeatherArtKinds.Cloudy, d.Art);
        Assert.Equal("阴", d.Text);
    }

    [Fact]
    public void WeatherCodes_DayNightOnlyChangesClear()
    {
        Assert.Equal("晴", WeatherCodes.Describe(0, isDay: true).Text);
        Assert.Equal("晴夜", WeatherCodes.Describe(0, isDay: false).Text);
        Assert.Equal(WeatherArtKinds.Clear, WeatherCodes.Describe(0, isDay: false).Art);

        // 雨雪的昼夜文案不该跟着变
        Assert.Equal(WeatherCodes.Describe(61, true).Text, WeatherCodes.Describe(61, false).Text);
        Assert.Equal(WeatherCodes.Describe(71, true).Text, WeatherCodes.Describe(71, false).Text);
    }

    [Theory]
    [InlineData(0, "北风")]
    [InlineData(45, "东北风")]
    [InlineData(90, "东风")]
    [InlineData(180, "南风")]
    [InlineData(270, "西风")]
    [InlineData(360, "北风")]
    [InlineData(-90, "西风")]   // 负角度也要夹回 0..360
    public void WindDirection_CompassPoints(double degrees, string expected) =>
        Assert.Equal(expected, WeatherCodes.WindDirection(degrees));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(15, 3)]
    [InlineData(100, 10)]
    [InlineData(120, 12)]
    public void WindScale_BeaufortBuckets(double kmh, int expected) =>
        Assert.Equal(expected, WeatherCodes.WindScale(kmh));

    // ————————————————————— Open-Meteo 解析 —————————————————————

    [Fact]
    public void WeatherClient_Parse_ReadsCurrentBlock()
    {
        const string json = """
        {"latitude":30.5,"current":{"temperature_2m":21.4,"relative_humidity_2m":64,
         "apparent_temperature":20.8,"is_day":1,"weather_code":61,
         "wind_speed_10m":8.4,"wind_direction_10m":230}}
        """;
        using var doc = JsonDocument.Parse(json);
        var loc = new WeatherLocation(30.66, 104.07, "四川省", "成都市", "", "pconline");

        var snap = WeatherClient.Parse(doc.RootElement, loc, 1_800_000_000);

        Assert.NotNull(snap);
        Assert.Equal(21.4, snap!.Temp);
        Assert.Equal(61, snap.Code);
        Assert.Equal("小雨", snap.Text);
        Assert.Equal(WeatherArtKinds.Rain, snap.Art);
        Assert.Equal("成都市", snap.Place);
        Assert.Equal("pconline", snap.Source);
        Assert.Contains("小雨", snap.Lines[0]);
        Assert.Contains("成都市", snap.Lines[1]);
    }

    [Fact]
    public void WeatherClient_Parse_NightClearDrawsMoon()
    {
        const string json = """{"current":{"temperature_2m":12,"weather_code":0,"is_day":0}}""";
        using var doc = JsonDocument.Parse(json);
        var loc = new WeatherLocation(30, 104, "四川省", "成都市", "", "manual");

        var snap = WeatherClient.Parse(doc.RootElement, loc, 0);

        Assert.NotNull(snap);
        Assert.False(snap!.IsDay);
        Assert.Equal("晴夜", snap.Text);
        Assert.Equal(WeatherArtKinds.Clear, snap.Art);
    }

    [Fact]
    public void WeatherClient_Parse_MissingFeelsLikeFallsBackToTemp()
    {
        const string json = """{"current":{"temperature_2m":18,"weather_code":3}}""";
        using var doc = JsonDocument.Parse(json);
        var loc = new WeatherLocation(30, 104, "四川省", "成都市", "", "manual");

        var snap = WeatherClient.Parse(doc.RootElement, loc, 0);

        Assert.NotNull(snap);
        Assert.Equal(18, snap!.Temp);
        Assert.Equal(18, snap.Feels);   // 缺体感就报温度，别给个 0℃ 的假数据
    }

    [Fact]
    public void WeatherClient_Parse_ReturnsNullWithoutCurrent()
    {
        using var doc = JsonDocument.Parse("""{"latitude":30.5}""");
        var loc = new WeatherLocation(30, 104, "四川省", "成都市", "", "manual");

        Assert.Null(WeatherClient.Parse(doc.RootElement, loc, 0));
    }

    // ————————————————————— 定位回退 —————————————————————

    [Fact]
    public void WeatherLocator_CityTableGivesRealCoordinates()
    {
        var loc = WeatherLocator.FromCityTable("四川省", "成都市", "");

        Assert.NotNull(loc);
        Assert.Equal("city-table", loc!.Source);
        Assert.True(loc.Lat is > 25 and < 35, $"纬度 {loc.Lat}");
        Assert.True(loc.Lon is > 95 and < 110, $"经度 {loc.Lon}");
    }

    [Fact]
    public void WeatherLocator_OnlyProvinceFallsBackToCapital()
    {
        // 默认配置只有「四川省」没有市 —— 不退到省会就永远拿不到坐标
        var loc = WeatherLocator.FromCityTable("四川省", "", "");

        Assert.NotNull(loc);
        Assert.False(string.IsNullOrEmpty(loc!.City));
    }

    [Fact]
    public void WeatherLocator_EmptyProvinceReturnsNull() =>
        Assert.Null(WeatherLocator.FromCityTable("", "", ""));

    // ————————————————————— 坐标反查行政区 —————————————————————

    /// <summary>
    /// IP 定位只给经纬度时靠它反推省/市/区县 —— 这是"定位结果连市都没有"的正解：
    /// 只要坐标落在国内，就一定能补出市，大多数情况还能补出区。
    /// </summary>
    [Fact]
    public void Cities_NearestDistrict_ResolvesChengdu()
    {
        var hit = Cities.NearestDistrict(30.6570, 104.0650);   // 成都天府广场附近

        Assert.NotNull(hit);
        Assert.Equal("四川省", hit!.Value.Province);
        Assert.Contains("成都", hit.Value.City);
        Assert.False(string.IsNullOrEmpty(hit.Value.District), "区县不该为空");
        Assert.True(hit.Value.DistanceKm < 40, $"最近区县 {hit.Value.DistanceKm:0.#}km");
    }

    [Fact]
    public void Cities_NearestDistrict_ResolvesBeijing()
    {
        var hit = Cities.NearestDistrict(39.9042, 116.4074);

        Assert.NotNull(hit);
        Assert.Contains("北京", hit!.Value.Province);
        Assert.True(hit.Value.DistanceKm < 60, $"最近区县 {hit.Value.DistanceKm:0.#}km");
    }

    [Fact]
    public void Cities_NearestCity_AlsoHasProvince()
    {
        // 区县太远时的退路：至少把省/市认对。
        // (30.9, 104.2) 在成都平原北部，最近的城市是德阳而不是成都 —— 这里只钉死"省份必须对"。
        var hit = Cities.NearestCity(30.9, 104.2);

        Assert.NotNull(hit);
        Assert.Contains("四川", hit!.Value.Province);
    }

    // ————————————————————— 跨省重名（真实踩过的坑） —————————————————————

    /// <summary>
    /// 城市表里存在跨省重名：**湖南益阳有个「资阳区」，四川有个「资阳市」**。
    /// 早期实现只按市名反查（先注册先赢），于是 ipip 明明回「四川/资阳」，
    /// 却被翻成「湖南/益阳」—— 定位直接偏到 1000 公里外。
    /// 这条测试就是钉死这个回归。
    /// </summary>
    [Fact]
    public void Cities_Canonicalize_PrefersTheGivenProvince()
    {
        var (prov, city) = Cities.Canonicalize("四川", "资阳");

        Assert.Equal("四川省", prov);
        Assert.Equal("资阳市", city);

        // 反证：只按市名反查确实会落到湖南（这就是不能只查市名的原因）
        Assert.Equal("湖南省", Cities.ProvinceOfCity("资阳").Province);
    }

    [Fact]
    public void WeatherLocator_ParseIpip_ResolvesZiyangNotYiyang()
    {
        // 实测的官方响应：location = [国家, 省, 市, 区县, 运营商]
        const string json = """
        {"ret":"ok","data":{"ip":"222.213.201.163","location":["中国","四川","资阳","","电信"]}}
        """;
        using var doc = JsonDocument.Parse(json);

        var loc = WeatherLocator.Parse("ipip", doc.RootElement);

        Assert.NotNull(loc);
        Assert.Equal("四川省", loc!.Province);
        Assert.Equal("资阳市", loc.City);
        Assert.True(loc.Lat is > 29 and < 31, $"纬度 {loc.Lat}");
        Assert.True(loc.Lon is > 104 and < 106, $"经度 {loc.Lon}");
        Assert.Equal("ipip", loc.Source);
    }

    // ————————————————————— 动画种类 —————————————————————

    [Theory]
    [InlineData("rain", "rain")]
    [InlineData("thunder", "thunder")]
    [InlineData("", "cloudy")]
    [InlineData("not-a-kind", "cloudy")]
    public void WeatherArt_UnknownKindNormalizesToCloudy(string? input, string expected) =>
        Assert.Equal(expected, WeatherArt.Normalize(input));

    // ————————————————————— 插件契约向后兼容 —————————————————————

    [Fact]
    public void BoardSection_TwoArgCtor_KeepsArtNullForOldPlugins()
    {
        // ★ 已发布插件（市场上的 lch.gaokao-countdown）走的就是 2 参主构造。
        //   只要它还在，老插件的二进制就不会 MissingMethod。
        var old = new BoardSection("标题", new[] { "一行" });
        Assert.Null(old.Art);

        var fresh = new BoardSection("标题", new[] { "一行" },
            new BoardArt("weather", "61", 21.4, IsDay: false, Animated: false));

        Assert.Equal("weather", fresh.Art!.Kind);
        Assert.Equal("61", fresh.Art.Code);
        Assert.Equal(21.4, fresh.Art.Value);
        Assert.False(fresh.Art.IsDay);
        Assert.False(fresh.Art.Animated);
    }

    [Fact]
    public void BoardArt_DefaultsAreSafeForHostsThatIgnoreIt()
    {
        // 旧宿主 / 不认识的 Kind：忽略 Art 只画正文，不该开天窗
        var art = new BoardArt("something-new");
        Assert.Equal("something-new", art.Kind);
        Assert.Equal("", art.Code);
        Assert.True(double.IsNaN(art.Value));
        Assert.True(art.IsDay);
        Assert.True(art.Animated);
    }
}
