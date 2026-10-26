using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;
using System.Text.Json.Nodes;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

public class StorageAndSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cdl-test-" + Guid.NewGuid().ToString("N"));

    public StorageAndSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void AtomicWrite_RoundTripsAndLeavesNoTempFile()
    {
        var path = P("schedule.json");
        var file = Defaults.CreateSchedule();
        AtomicJson.Write(path, file);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bad"));

        var read = AtomicJson.Read(path, Defaults.CreateSchedule);
        Assert.Equal(file.Active, read.Active);
        Assert.Equal(file.ActiveTimetable.Periods.Count, read.ActiveTimetable.Periods.Count);

        // 覆盖写
        read.AdvanceMinutes = 7;
        AtomicJson.Write(path, read);
        Assert.Equal(7, AtomicJson.Read(path, Defaults.CreateSchedule).AdvanceMinutes);
    }

    [Fact]
    public void AtomicWrite_QuarantinesCorruptJson()
    {
        var path = P("homework.json");
        File.WriteAllText(path, "{ this is not json ");
        var items = AtomicJson.Read(path, Defaults.CreateHomework);
        Assert.NotEmpty(items);
        Assert.True(File.Exists(path + ".bad"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Settings_TypeMismatch_FallsBackToDefault_AndDoesNotCorruptFile()
    {
        var path = P("settings.json");
        File.WriteAllText(path, """
        { "main_font_size": "很大", "show_island": 1, "island_top_margin": 30,
          "theme": 12345, "unknown_future_key": { "a": [1,2] }, "island_alert_hold_ms": 900 }
        """);

        var paths = new DataPaths(_dir);
        var mgr = new SettingsManager(paths);
        mgr.Load();

        Assert.Equal(16, mgr.Int("main_font_size"));           // 字符串非法 -> 默认值
        Assert.True(mgr.Bool("show_island"));                   // 1 -> true
        Assert.Equal(30, mgr.Int("island_top_margin"));
        Assert.Equal("system", mgr.Str("theme"));               // 数字非法 -> 默认值
        Assert.Equal(900, mgr.Int("island_alert_hold_ms"));
        Assert.Contains("main_font_size", mgr.RepairedKeys);
        Assert.True(mgr.Current.Extras.ContainsKey("unknown_future_key"));  // 未知键保留

        // 保存后未知键仍在，坏值不写回
        mgr.Set("main_opacity", 500);   // 越界 -> 钳到 100
        Assert.Equal(100, mgr.Int("main_opacity"));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(100, (int)json["main_opacity"]!);
        Assert.True(json.ContainsKey("unknown_future_key"));
    }

    [Fact]
    public void Settings_ChangedEventCarriesKeys()
    {
        var mgr = new SettingsManager(new DataPaths(_dir));
        mgr.Load();
        var keys = new List<string>();
        mgr.SettingsChanged += (_, e) => keys.AddRange(e.Keys);
        Assert.True(mgr.Set("island_countdown_sec", 120));
        Assert.False(mgr.Set("island_countdown_sec", 120));   // 未变化不广播
        Assert.Equal(new[] { "island_countdown_sec" }, keys);
        Assert.Equal(120, JsonNode.Parse(File.ReadAllText(P("settings.json")))!["island_countdown_sec"]!.GetValue<int>());
    }

    [Fact]
    public void Settings_ResetPage_RestoresOnlyThatPage()
    {
        var mgr = new SettingsManager(new DataPaths(_dir));
        mgr.Load();
        mgr.Set("main_font_size", 24);
        mgr.Set("island_top_margin", 30);
        mgr.ResetPage(AppSettings.PageAppearance);
        Assert.Equal(24, mgr.Int("main_font_size"));      // 通用页保持不变
        Assert.Equal(30, mgr.Int("island_top_margin"));   // 灵动岛页保持不变
        Assert.Equal("system", mgr.Str("theme"));         // 本页已恢复默认
        Assert.Equal(220, mgr.Int("animation_duration_ms"));
    }

    [Fact]
    public void Duty_RollsOverOnNewDay()
    {
        var paths = new DataPaths(_dir);
        var duty = new StudentOnDuty(paths);
        duty.Load();
        Assert.Equal("张三", duty.TodayName);

        // 模拟跨天：last_date 写成昨天
        File.WriteAllText(paths.Config, """{"current_index":0,"last_date":"2000-01-01","should":"40","actual":"0"}""");
        duty.Load();
        Assert.Equal("李四", duty.TodayName);              // 自动顺延一位

        duty.Next();
        Assert.Equal("王五", duty.TodayName);
        duty.Prev();
        Assert.Equal("李四", duty.TodayName);

        duty.SetAttendance("45", "43");
        var reread = new StudentOnDuty(paths);
        reread.Load();
        Assert.Equal(45, reread.Config.ShouldCount);
        Assert.Equal(43, reread.Config.ActualCount);
        Assert.Equal("李四", reread.TodayName);
    }
}
