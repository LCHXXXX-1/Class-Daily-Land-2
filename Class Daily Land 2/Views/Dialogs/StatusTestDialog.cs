using ClassDailyLand2.App.Controls;
using ClassDailyLand2.Core.Engine;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 状态测试（对位 web 版 openStatusTest）：试算完全在宿主侧算 ——
/// ScheduleEngine.Compute 是权威，页面只画结果，不在别处重抄状态机。
/// </summary>
public sealed class StatusTestDialog : DialogPanel
{
    readonly TextBox _time;
    readonly NumberBox _offset;
    readonly NumberBox _advance;
    readonly NumberBox _countdown;
    readonly Border _capsule = new();
    readonly TextBlock _capsuleText = new();
    readonly TextBlock _detail = new() { Style = S("EdEmpty"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

    public StatusTestDialog(ViewModels.SettingsViewModel vm) : base(vm, "状态测试",
        "填一个时间点，按当前课表与参数试算灵动岛会显示什么。", 560)
    {
        _time = TimeInput(DateTime.Now.ToString("HH:mm"));
        _advance = Num(vm.ScheduleAdvanceMinutes, 0, 60, " 分钟");
        _offset = Num(vm.ScheduleTimeOffsetSeconds, -1800, 1800, " 秒");
        _countdown = Num(vm.IslandCountdownSec, 5, 600, " 秒");

        Add(Row(Label("时间点"), _time, Label("提前提醒"), _advance));
        Add(Row(Label("时间偏移"), _offset, Label("倒计时"), _countdown));

        // 预览胶囊：沿用原生岛配色（#1c1c1e 底 / 蓝倒计时 / 绿进行中）
        _capsule.Height = 40;
        _capsule.Width = 320;
        _capsule.Margin = new Thickness(0, 10, 0, 0);
        _capsule.HorizontalAlignment = HorizontalAlignment.Left;
        _capsule.CornerRadius = new CornerRadius(20);
        _capsule.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e));
        _capsuleText.FontSize = 14;
        _capsuleText.FontWeight = FontWeights.SemiBold;
        _capsuleText.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
        _capsuleText.VerticalAlignment = VerticalAlignment.Center;
        _capsuleText.Margin = new Thickness(46, 0, 14, 0);
        _capsuleText.Text = "点「试算」看结果";
        _capsule.Child = _capsuleText;
        Add(_capsule);

        Add(_detail);

        AddButton("关闭", Close);
        AddButton("保存为当前参数", SaveParams);
        AddButton("试算", Compute, primary: true);
    }

    static NumberBox Num(int value, int min, int max, string suffix) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Suffix = suffix,
        Width = 120,
    };

    void Compute()
    {
        var parts = _time.Text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
        {
            Tell(this, "时间请写成 HH:MM。", "状态测试");
            return;
        }

        var now = DateTime.Today.AddHours(h).AddMinutes(m);
        var status = Ctx.Schedule.Engine.Compute(now, _advance.Value, _offset.Value);
        var text = ScheduleText.Core(status);

        _capsuleText.Text = text;
        var ring = status.IsOngoing
            ? Color.FromRgb(0x30, 0xd1, 0x58)
            : status.IsUpcoming ? Color.FromRgb(0x4d, 0xa3, 0xff) : Color.FromRgb(0x8a, 0x8a, 0x8e);
        _capsule.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e));

        var ratio = status.IsOngoing && status.DurationSec > 0
            ? 1.0 - (double)status.RemainSec / status.DurationSec
            : status.IsUpcoming
                ? 1.0 - Math.Clamp((double)status.UntilSec / Math.Max(1, Math.Max(_advance.Value * 60, _countdown.Value)), 0, 1)
                : 0.0;
        var countdown = status.IsUpcoming && status.UntilSec <= _countdown.Value;

        _detail.Text =
            $"状态：{status.Status}    节次：{(status.Index < 0 ? "—" : (status.Index + 1).ToString())}    " +
            $"课程：{(string.IsNullOrEmpty(status.Course) ? "—" : status.Course)}\n" +
            $"环颜色：#{ring.R:X2}{ring.G:X2}{ring.B:X2}    环比例：{ratio:P0}    " +
            $"倒计时态：{(countdown ? "是（胶囊宽 600）" : "否（胶囊宽 240 + 插件位）")}\n" +
            $"剩余 / 距离：{(status.IsOngoing ? status.RemainMin + " 分钟" : status.UntilMin + " 分钟")}";
    }

    void SaveParams()
    {
        Vm.ScheduleAdvanceMinutes = _advance.Value;
        Vm.ScheduleTimeOffsetSeconds = _offset.Value;
        Vm.IslandCountdownSec = _countdown.Value;
        Tell(this, "已写入 settings.json，灵动岛按新参数运行。", "状态测试");
    }
}
