using ClassDailyLand.Core;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Storage;
using Xunit;

namespace ClassDailyLand.Core.Tests;

/// <summary>
/// 设置中心（XAML 版）依赖的模型不变量：
/// 页面靠 PageKeys 做「本页恢复默认」，靠 AppSettings 的类型校验挡住坏值，
/// 这两条一旦破了，界面上就是「改了没生效」或「恢复默认漏项」。
/// </summary>
public class SettingsCenterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cdl-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsCenterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void PageKeys_OnlyReferenceKnownKeys()
    {
        var known = AppSettings.All.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (page, keys) in AppSettings.PageKeys)
            foreach (var key in keys)
                Assert.True(known.Contains(key), $"{page} 页引用了不存在的设置键 {key}");
    }

    [Fact]
    public void AppearancePage_OwnsEveryAppearanceKey()
    {
        // 外观页漏掉任何一个键，「本页恢复默认」就不会把它复原
        var owned = AppSettings.PageKeys[AppSettings.PageAppearance].ToHashSet(StringComparer.Ordinal);
        Assert.Contains("theme", owned);
        Assert.Contains("fade_animation", owned);
        Assert.Contains("animation_duration_ms", owned);
        Assert.Contains("anim_fade_panel", owned);
        Assert.Contains("anim_fade_text", owned);
        Assert.Contains("settings_mica", owned);
    }

    [Fact]
    public void NewKeys_HaveUsableDefaultsAndRanges()
    {
        var meta = AppSettings.All.ToDictionary(m => m.Key, StringComparer.Ordinal);

        Assert.Equal(true, meta["anim_fade_panel"].Default);
        Assert.Equal(true, meta["anim_fade_text"].Default);
        // 云母/亚克力背板默认开启（Win11 走云母，Win10 1803+ 走亚克力，更老的会回退成不透明）
        Assert.Equal(true, meta["settings_mica"].Default);
        Assert.Equal(true, meta["block_update_on_source"].Default);

        var settings = new AppSettings();
        Assert.True(settings.GetBool("block_update_on_source"));
        Assert.True(settings.GetBool("settings_mica"));

        // 越界要夹住，不能把非法值写进 JSON
        settings.Set("animation_duration_ms", 99999);
        Assert.Equal(2000, settings.GetInt("animation_duration_ms"));
    }

    [Fact]
    public void SettingsManager_ResetAll_RestoresDefaultsAndBroadcastsOnce()
    {
        var paths = DataPaths.CreateAt(_dir);
        var manager = new SettingsManager(paths);
        manager.Load();

        manager.Set("show_island", false);
        manager.Set("theme", "dark");
        Assert.False(manager.Bool("show_island"));
        Assert.Equal("dark", manager.Str("theme"));

        var broadcasts = 0;
        List<string>? lastKeys = null;
        manager.SettingsChanged += (_, e) => { broadcasts++; lastKeys = e.Keys.ToList(); };

        manager.ResetAll();

        Assert.Equal(1, broadcasts);
        Assert.NotNull(lastKeys);
        Assert.Contains("show_island", lastKeys!);
        Assert.True(manager.Bool("show_island"));
        Assert.Equal("system", manager.Str("theme"));

        // 落盘内容与内存一致
        var reread = new SettingsManager(paths);
        reread.Load();
        Assert.Equal("system", reread.Str("theme"));
    }

    [Fact]
    public void SettingsManager_KeepsUnknownKeysSofterForwardCompat()
    {
        var paths = DataPaths.CreateAt(_dir);
        var manager = new SettingsManager(paths);
        manager.Load();
        manager.Set("some_future_key", 42);
        Assert.Equal(42, Convert.ToInt32(manager.Current.Extras["some_future_key"]));

        var reread = new SettingsManager(paths);
        reread.Load();
        Assert.True(reread.Current.Extras.ContainsKey("some_future_key"));
    }
}
