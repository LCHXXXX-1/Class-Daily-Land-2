using ClassDailyLand2.App.Integrations.Disaster;
using System.Text.Json;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 灾害预警移植的纯逻辑部分：烈度/倒计时算法、城市检索、三个数据源的解析与合并。
/// 期望值都是从 Python 版 <c>quake_calc.py</c> 直接算出来的，用于确认移植没有走样。
/// </summary>
public sealed class DisasterIntegrationTests
{
    // ————————————————————— 算法 —————————————————————

    [Fact]
    public void Haversine_MatchesPython()
    {
        // 成都 → 北京
        var d = QuakeCalc.HaversineKm(30.6595, 104.0658, 39.9042, 116.4074);
        Assert.InRange(d, 1519.3, 1519.4);
    }

    [Fact]
    public void Intensity_MatchesPython()
    {
        Assert.Equal(5.623881, QuakeCalc.IntensityAt(6.4, 50), 5);
        // 距离越远烈度越小，且不会为负
        Assert.True(QuakeCalc.IntensityAt(6.4, 100) < QuakeCalc.IntensityAt(6.4, 50));
        Assert.Equal(0, QuakeCalc.IntensityAt(1.0, 2000));
    }

    [Theory]
    [InlineData(10, 0, 2.976)]        // 深度 10km 落在第 2 档，查表 row[0]
    [InlineData(10, 10, 4.899733)]
    [InlineData(10, 50, 15.165412)]
    [InlineData(10, 100, 29.875876)]
    [InlineData(10, 200, 54.555761)]
    [InlineData(10, 500, 123.406527)]
    [InlineData(10, 2181.449, 518.848)]   // 表尾最后一点
    [InlineData(10, 2500, 590.057801)]    // 超出表尾走线性外推
    [InlineData(0, 30, 8.918986)]
    [InlineData(0, 120, 35.677894)]
    public void Countdown_MatchesPython(double depth, double distance, double expected)
    {
        Assert.Equal(expected, QuakeCalc.CountdownSeconds(depth, distance), 5);
    }

    [Fact]
    public void Countdown_RejectsNegativeInput() =>
        Assert.Equal(0, QuakeCalc.CountdownSeconds(-1, 100));

    [Fact]
    public void Level_ThresholdsMatchPython()
    {
        Assert.Equal(QuakeLevels.Blue, QuakeCalc.LevelOf(2.0));
        Assert.Equal(QuakeLevels.Yellow, QuakeCalc.LevelOf(2.01));
        Assert.Equal(QuakeLevels.Yellow, QuakeCalc.LevelOf(3.0));
        Assert.Equal(QuakeLevels.Orange, QuakeCalc.LevelOf(5.0));
        Assert.Equal(QuakeLevels.Red, QuakeCalc.LevelOf(5.01));
    }

    [Fact]
    public void Level_CustomThresholds()
    {
        var t = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(QuakeLevels.Red, QuakeCalc.LevelOf(3.5, t));
        Assert.Equal(QuakeLevels.Blue, QuakeCalc.LevelOf(1.0, t));
    }

    [Fact]
    public void LevelHelpers_Work()
    {
        Assert.True(QuakeCalc.LevelAtLeast("orange", "yellow"));
        Assert.False(QuakeCalc.LevelAtLeast("blue", "yellow"));
        Assert.Equal("橙色", QuakeCalc.LevelWord("orange"));
        Assert.Equal("#ff453a", QuakeCalc.LevelColor("red"));
    }

    [Fact]
    public void FmtClock_UsesBeijingTime()
    {
        // 2026-10-24 12:00:00 UTC = 20:00:00 北京时间
        var ts = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal("20:00:00", QuakeCalc.FmtClock(ts));
        Assert.Equal("20:00", QuakeCalc.FmtClockHm(ts));
    }

    [Fact]
    public void Evaluate_TriggeredWhenWaveIncomingWithinLead()
    {
        var now = 1_800_000_000d;
        // 震中在成都正北 100km 左右，10 秒前发震
        var ev = new QuakeEvent("t:1", "icl", "1", 1, now - 10, now,
            30.6595 + 0.9, 104.0658, 10, 6.5, "四川成都");

        var r = QuakeCalc.Evaluate(ev, 30.6595, 104.0658, now, 2.0);

        Assert.NotNull(r);
        Assert.Equal("incoming", r!.Phase);
        Assert.True(r.Triggered, $"烈度 {r.LocalIntensity} 距 {r.DistanceKm}");
        Assert.True(r.Displayable);
        Assert.True(r.RemainSec > 0);
    }

    [Fact]
    public void Evaluate_NotTriggeredWhenIntensityBelowThreshold()
    {
        var now = 1_800_000_000d;
        var ev = new QuakeEvent("t:2", "icl", "2", 1, now - 5, now, 39.9, 116.4, 10, 4.0, "北京");

        var r = QuakeCalc.Evaluate(ev, 30.6595, 104.0658, now, 2.0);

        Assert.NotNull(r);
        Assert.False(r!.Triggered);
        Assert.False(r.Displayable);
    }

    [Fact]
    public void Evaluate_NullWithoutCoordinates()
    {
        var ev = new QuakeEvent("t:3", "icl", "3", 1, 0, 0, 30, 104, 10, 6, "");
        Assert.Null(QuakeCalc.Evaluate(ev, null, null, 0));
    }

    // ————————————————————— 城市表 —————————————————————

    [Fact]
    public void Cities_HasAllProvinces()
    {
        var provinces = Cities.Provinces;
        Assert.Equal(31, provinces.Count);
        Assert.Contains("四川省", provinces);
    }

    [Fact]
    public void Cities_LookupByProvinceAndCity()
    {
        Assert.Contains("成都市", Cities.CitiesOf("四川省"));

        var coord = Cities.CoordOf("四川省", "成都市");
        Assert.NotNull(coord);
        Assert.InRange(coord!.Value.Lat, 30.0, 31.5);
        Assert.InRange(coord.Value.Lon, 103.5, 104.7);
    }

    [Fact]
    public void Cities_ShortNameAndReverseLookup()
    {
        Assert.Equal("成都", Cities.ShortName("成都市"));
        Assert.Equal("甘孜藏族", Cities.ShortName("甘孜藏族自治州"));

        var hit = Cities.ProvinceOfCity("成都市");
        Assert.Equal("四川省", hit.Province);
    }

    [Fact]
    public void Cities_DistrictsOfCity()
    {
        var districts = Cities.DistrictsOf("四川省", "成都市");
        Assert.NotEmpty(districts);
    }

    // ————————————————————— 解析 —————————————————————

    [Fact]
    public void ParseIcl_ReadsListPayload()
    {
        var json = """
        {"data":[{"eventId":123,"latitude":30.1,"longitude":103.9,"magnitude":5.2,
                  "depth":12,"startAt":1800000000000,"updateAt":1800000030000,
                  "epicenter":"四川雅安","updates":3,"epiIntensity":4.5}]}
        """;
        using var doc = JsonDocument.Parse(json);

        var events = DisasterSources.ParseIclEvents(doc.RootElement);

        var ev = Assert.Single(events);
        Assert.Equal("icl:123", ev.Uid);
        Assert.Equal(1800000000, ev.OriginTs);
        Assert.Equal(3, ev.Revision);
        Assert.Equal(5.2, ev.Magnitude);
        Assert.Equal("四川雅安", ev.Epicenter);
    }

    [Fact]
    public void ParseWolfx_ToleratesFieldVariants()
    {
        var json = """
        {"EventID":"2026102401","Latitude":30.5,"Longitude":104.1,"Magunitude":4.8,
         "Depth":10,"OriginTime":"2026-10-24 20:00:10","ReportNum":2,"HypoCenter":"四川眉山"}
        """;
        using var doc = JsonDocument.Parse(json);

        var events = DisasterSources.ParseWolfxEvents(doc.RootElement, "wolfx_cenc");

        var ev = Assert.Single(events);
        Assert.Equal("wolfx_cenc:2026102401", ev.Uid);
        Assert.Equal(4.8, ev.Magnitude);
        Assert.Equal(2, ev.Revision);
        // "2026-10-24 20:00:10" 北京时间 → epoch
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 10, TimeSpan.Zero).ToUnixTimeSeconds(), ev.OriginTs);
    }

    [Fact]
    public void ParseNmc_ExtractsSignalLevelCounty()
    {
        var json = """
        {"data":{"page":{"list":[
          {"alertid":"x1","title":"成都市气象台发布暴雨橙色预警信号","issuetime":"2026-10-24 18:30:00","url":"u"}
        ]}}}
        """;
        using var doc = JsonDocument.Parse(json);

        var alerts = DisasterSources.ParseNmcAlerts(doc.RootElement, "四川省", strict: false);

        var a = Assert.Single(alerts);
        Assert.Equal("nmc:x1", a.Uid);
        Assert.Equal(QuakeLevels.Orange, a.Level);
        Assert.Equal("暴雨", a.Signal);
        Assert.Equal("成都市", a.County);
    }

    [Fact]
    public void ParseNmc_StrictKeepsOnlyProvincePrefixed()
    {
        // strict 的语义是"标题以省名开头"（全国列表兜底时用），
        // 而 nmc 的标题一般是「成都市气象台发布…」，所以 strict 下会被过滤掉 —— 与 Python 版一致。
        var json = """
        {"data":{"page":{"list":[
          {"alertid":"a","title":"成都市气象台发布暴雨黄色预警信号"},
          {"alertid":"b","title":"广东省气象台发布台风红色预警信号"},
          {"alertid":"c","title":"四川省成都市气象台发布大风蓝色预警信号"}
        ]}}}
        """;
        using var doc = JsonDocument.Parse(json);

        var strict = DisasterSources.ParseNmcAlerts(doc.RootElement, "四川省", strict: true);
        var loose = DisasterSources.ParseNmcAlerts(doc.RootElement, "四川省", strict: false);

        var only = Assert.Single(strict);
        Assert.Equal("nmc:c", only.Uid);
        Assert.Equal(3, loose.Count);

        // 省名前缀会被剥掉
        Assert.Equal("成都市", only.County);
    }

    // ————————————————————— 合并 —————————————————————

    [Fact]
    public void Merge_DedupesByApproximateLocation()
    {
        var existing = new Dictionary<string, QuakeEvent>
        {
            ["icl:1"] = new("icl:1", "icl", "1", 1, 1_800_000_000, 1_800_000_000, 30, 104, 10, 5.0, "A"),
        };
        // 另一个源报的同一地震：位置/时间/震级都近似
        var incoming = new[]
        {
            new QuakeEvent("wolfx_sc:9", "wolfx_sc", "9", 1, 1_800_000_030, 1_800_000_030, 30.1, 104.1, 10, 5.2, "A"),
        };

        var (merged, changed) = DisasterSources.MergeQuakes(existing, incoming);

        Assert.Single(merged);
        Assert.Contains("icl:1", merged.Keys);              // uid 保持稳定
        Assert.Single(changed);
    }

    [Fact]
    public void Merge_HigherRevisionWins()
    {
        var existing = new Dictionary<string, QuakeEvent>
        {
            ["icl:1"] = new("icl:1", "icl", "1", 1, 1_800_000_000, 1_800_000_000, 30, 104, 10, 5.0, "A"),
        };
        var incoming = new[]
        {
            new QuakeEvent("icl:1", "icl", "1", 2, 1_800_000_000, 1_800_000_060, 30, 104, 10, 5.6, "A"),
        };

        var (merged, changed) = DisasterSources.MergeQuakes(existing, incoming);

        Assert.Equal(5.6, merged["icl:1"].Magnitude);
        Assert.Single(changed);
    }

    [Fact]
    public void Prune_DropsExpiredEvents()
    {
        var now = 1_800_000_000d;
        var events = new Dictionary<string, QuakeEvent>
        {
            ["old"] = new("old", "icl", "old", 1, now - 1000, now, 30, 104, 10, 5, ""),
            ["new"] = new("new", "icl", "new", 1, now - 10, now, 30, 104, 10, 5, ""),
        };

        var pruned = DisasterSources.PruneQuakes(events, now);

        Assert.Single(pruned);
        Assert.True(pruned.ContainsKey("new"));
    }
}
