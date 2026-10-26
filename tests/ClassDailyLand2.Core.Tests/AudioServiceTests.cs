using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.Core.Storage;
using System.Reflection;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 媒体片段「暂停 5 秒就收起、重新播放再加长」的行为。
///
/// 这里守的是一个特别容易回退的点：**暂停时 SMTC 依然报着 title/artist**，
/// 所以不能拿"info 有没有内容"判断该不该显示 —— 旧实现在暂停后永远收不起来。
/// 判定必须看"最后一次真正在播放的时刻"。
/// </summary>
public sealed class AudioServiceTests
{
    static AudioService MakeService()
    {
        // 临时目录：AudioService 会往 Root\integrations\audio 放封面
        var root = Path.Combine(Path.GetTempPath(), "cdl-audio-" + Guid.NewGuid().ToString("N"));
        var paths = DataPaths.CreateAt(root);
        return new AudioService(paths, new Logger(paths) { Enabled = false });
    }

    /// <summary>把"最后一次在播放"的时刻往前拨，模拟时间流逝（不改真实时钟，测试才跑得快）。</summary>
    static void Backdate(AudioService svc, double seconds)
    {
        var field = typeof(AudioService).GetField("_lastPlayingUtc",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(svc, DateTime.UtcNow.AddSeconds(-seconds));
    }

    [Fact]
    public void Playing_ShowsMedia()
    {
        using var svc = MakeService();
        svc.InjectTestMedia("夜曲", "周杰伦");

        var d = svc.Display;
        Assert.NotNull(d);
        Assert.Equal("夜曲", d!.Title);
        Assert.True(d.Playing);
    }

    [Fact]
    public void Paused_KeepsMediaWithinKeepWindow()
    {
        using var svc = MakeService();
        svc.InjectTestMedia("夜曲", "周杰伦");
        svc.TogglePlayPause();                 // 暂停

        var d = svc.Display;
        Assert.NotNull(d);                     // 5 秒内还留着
        Assert.False(d!.Playing);              // 按钮要变回 ▶
        Assert.Equal("夜曲", d.Title);
    }

    [Fact]
    public void Paused_HidesAfterKeepWindow()
    {
        using var svc = MakeService();
        svc.InjectTestMedia("夜曲", "周杰伦");
        svc.TogglePlayPause();                 // 暂停
        Backdate(svc, 6);                      // 已经过去 6 秒（默认保留 5 秒）

        Assert.Null(svc.Display);
        Assert.False(svc.HasMedia);
    }

    [Fact]
    public void ResumingAfterHidden_BringsMediaBack()
    {
        using var svc = MakeService();
        svc.InjectTestMedia("夜曲", "周杰伦");
        svc.TogglePlayPause();                 // 暂停
        Backdate(svc, 30);                      // 早就该收起了
        Assert.Null(svc.Display);

        svc.TogglePlayPause();                 // 重新播放
        var d = svc.Display;
        Assert.NotNull(d);
        Assert.True(d!.Playing);
    }

    [Fact]
    public void HideAfterZero_NeverKeepsPausedItem()
    {
        using var svc = MakeService();
        svc.InjectTestMedia("夜曲", "周杰伦");

        // 0 表示"暂停就立刻收起"，连一帧都不留。
        // HideAfterSec 是自动属性（private set），反射拿不到同名字段，要用属性 setter。
        var prop = typeof(AudioService).GetProperty(nameof(AudioService.HideAfterSec));
        Assert.NotNull(prop);
        prop!.SetValue(svc, 0);

        svc.TogglePlayPause();
        Assert.Null(svc.Display);
    }
}
