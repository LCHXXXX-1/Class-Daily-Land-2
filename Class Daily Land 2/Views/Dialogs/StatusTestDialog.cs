using ClassDailyLand2.App.Controls;
using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 状态测试：试算 + 演示。
///
/// 「试算」= 填一个时间点看结果。
/// 「演示」= 把「倒计时 → 上课 → 下课」整段按倍速跑一遍，**演在屏幕上的真实灵动岛上**
/// （由 AppController 驱动），这个对话框只是控制台：倍速、暂停、重播。
/// 对话框里的胶囊只是同步回显，方便对照。
///
/// 今天没课 / 已放学时自动改用"模拟课堂"（倒计时 60 秒 → 上课 45 分钟 → 下课），
/// 保证任何时间都能看完整流程。
/// </summary>
public sealed class StatusTestDialog : DialogPanel
{
    readonly TextBox _time;
    readonly NumberBox _offset;
    readonly NumberBox _advance;
    readonly NumberBox _countdown;
    readonly ComboBox _speed;

    readonly Border _capsule = new();
    readonly TextBlock _capsuleText = new();
    readonly ProgressBar _timeline = new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 100,
        Width = 320,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 10, 0, 0),
        Value = 0,
    };
    readonly TextBlock _stage = new() { Style = S("EdEmpty"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _detail = new() { Style = S("EdEmpty"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly Button _playButton;

    /// <summary>倍速档位（1 秒真实时间 = N 秒模拟时间）。</summary>
    static readonly double[] Speeds = { 1, 2, 5, 10, 30, 60, 120 };
    static readonly string[] SpeedLabels = { "1×", "2×", "5×", "10×", "30×", "60×", "120×" };

    double _speedValue = 60;
    DateTime _simStart;
    double _simTotal;
    Func<DateTime, ScheduleStatus>? _provider;
    bool _simulated;

    public StatusTestDialog(ViewModels.SettingsViewModel vm) : base(vm, "状态测试",
        "试算某时刻，或按倍速在灵动岛上完整演示「倒计时 → 上课 → 下课」。", 560)
    {
        _time = TimeInput(DateTime.Now.ToString("HH:mm"));
        _advance = Num(vm.ScheduleAdvanceMinutes, 0, 60, " 分钟");
        _offset = Num(vm.ScheduleTimeOffsetSeconds, -1800, 1800, " 秒");
        _countdown = Num(vm.IslandCountdownSec, 5, 600, " 秒");
        _speed = Select(SpeedLabels, 5);      // 默认 60×：一节 45 分钟的课约 46 秒看完
        _speed.SelectionChanged += (_, _) => OnSpeedChanged();

        Add(Row(Label("时间点"), _time, Label("提前提醒"), _advance));
        Add(Row(Label("时间偏移"), _offset, Label("倒计时"), _countdown));

        // 回显胶囊：与真实灵动岛同步，方便对照（岛本身才是主角）
        _capsule.Height = 40;
        _capsule.Width = 320;
        _capsule.Margin = new Thickness(0, 12, 0, 0);
        _capsule.HorizontalAlignment = HorizontalAlignment.Left;
        _capsule.CornerRadius = new CornerRadius(20);
        _capsule.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e));
        _capsuleText.FontSize = 14;
        _capsuleText.FontWeight = FontWeights.SemiBold;
        _capsuleText.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
        _capsuleText.VerticalAlignment = VerticalAlignment.Center;
        _capsuleText.Margin = new Thickness(46, 0, 14, 0);
        _capsuleText.Text = "点「在灵动岛演示」看完整流程";
        _capsule.Child = _capsuleText;
        Add(_capsule);

        Add(_timeline);
        Add(_stage);
        Add(_detail);

        _playButton = SmallButton("▶ 在灵动岛演示", TogglePlay);
        Add(Row(_playButton, SmallButton("↺ 重播", Restart), Label("倍速"), _speed));

        _uiTimer.Tick += (_, _) => OnUiTick();
        Unloaded += (_, _) =>
        {
            _uiTimer.Stop();
            Ctx.StopDemo();          // 关掉对话框就把岛还回真实课表
        };

        AddButton("关闭", Close);
        AddButton("保存为当前参数", SaveParams);
        AddButton("试算此刻", Compute, primary: true);
    }

    static NumberBox Num(int value, int min, int max, string suffix) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Suffix = suffix,
        Width = 120,
    };

    // ————————————————————— 试算（单点） —————————————————————

    void Compute()
    {
        if (!TryParseTime(out var baseTime)) return;
        var status = Ctx.Schedule.Engine.Compute(baseTime, _advance.Value, _offset.Value);
        Render(baseTime, status, single: true);
    }

    bool TryParseTime(out DateTime time)
    {
        time = default;
        var parts = _time.Text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)
            || h is < 0 or > 23 || m is < 0 or > 59)
        {
            Tell(this, "时间请写成 HH:MM。", "状态测试");
            return false;
        }
        time = DateTime.Today.AddHours(h).AddMinutes(m);
        return true;
    }

    // ————————————————————— 演示（驱动真实灵动岛） —————————————————————

    void TogglePlay()
    {
        if (Ctx.DemoRunning)
        {
            // 正在跑：暂停 / 继续
            if (_uiTimer.IsEnabled)
            {
                Ctx.PauseDemo();
                _uiTimer.Stop();
                _playButton.Content = "▶ 继续演示";
            }
            else
            {
                Ctx.ResumeDemo();
                _uiTimer.Start();
                _playButton.Content = "⏸ 暂停演示";
            }
            return;
        }

        StartDemo();
    }

    void Restart() => StartDemo();

    void StartDemo()
    {
        if (!TryParseTime(out var baseTime)) return;

        var probe = Ctx.Schedule.Engine.Compute(baseTime, _advance.Value, _offset.Value);

        DateTime start;
        double total;

        if (probe.IsOngoing)
        {
            start = baseTime;
            total = Math.Max(5, probe.RemainSec + 4);
            _provider = t => Ctx.Schedule.Engine.Compute(t, _advance.Value, _offset.Value);
            _simulated = false;
        }
        else if (probe.IsUpcoming)
        {
            // 从「倒计时开始前 2 秒」起跑，保证能看见倒计时那一段
            var lead = Math.Min(probe.UntilSec, Math.Max(5, _countdown.Value) + 2);
            start = baseTime.AddSeconds(probe.UntilSec - lead);
            var atStart = Ctx.Schedule.Engine.Compute(start, _advance.Value, _offset.Value);
            var dur = atStart.DurationSec > 0 ? atStart.DurationSec : 45 * 60;
            total = lead + dur + 4;
            _provider = t => Ctx.Schedule.Engine.Compute(t, _advance.Value, _offset.Value);
            _simulated = false;
        }
        else
        {
            // 今天没课 / 已经放学：走"模拟课堂"，岛上照样能看到完整三阶段
            start = baseTime;
            total = Math.Max(5, _countdown.Value) + 45 * 60 + 4;
            _provider = FakeClassStatus;
            _simulated = true;
        }

        _simStart = start;
        _simTotal = total;

        // ★ 演在真实灵动岛上（主程序驱动），对话框只是控制台
        if (_simulated) Ctx.Island?.Show();
        Ctx.StartDemo(_provider, start, total, _speedValue);

        _playButton.Content = "⏸ 暂停演示";
        _uiTimer.Start();
        Render(start);
    }

    void OnUiTick()
    {
        if (!Ctx.DemoRunning)
        {
            _uiTimer.Stop();
            _playButton.Content = "▶ 重新演示";
            return;
        }

        Render(Ctx.DemoClock);
    }

    /// <summary>改倍速：交给主程序换算，进度不会跳。</summary>
    void OnSpeedChanged()
    {
        var index = Math.Clamp(_speed.SelectedIndex, 0, Speeds.Length - 1);
        _speedValue = Speeds[index];
        Ctx.DemoSpeed = _speedValue;
    }

    /// <summary>
    /// 模拟课堂：倒计时 _countdown 秒 → 上课 45 分钟 → 下课。
    /// 字段口径与 ScheduleEngine 保持一致，岛上的显示逻辑不需要任何特判。
    /// </summary>
    ScheduleStatus FakeClassStatus(DateTime t)
    {
        const int classSec = 45 * 60;
        var countdown = Math.Max(5, _countdown.Value);
        var elapsed = (t - _simStart).TotalSeconds;

        if (elapsed < countdown)
        {
            var left = Math.Max(0, countdown - elapsed);
            return new ScheduleStatus("upcoming", "演示课程", "演示", "08:00", "08:45",
                (int)Math.Ceiling(left), Math.Max(1, (int)Math.Ceiling(left / 60)), countdown,
                0, 0, classSec, 0, null);
        }

        if (elapsed < countdown + classSec)
        {
            var remain = countdown + classSec - elapsed;
            return new ScheduleStatus("ongoing", "演示课程", "演示", "08:00", "08:45",
                0, 0, countdown,
                (int)Math.Ceiling(remain), Math.Max(1, (int)Math.Ceiling(remain / 60)), classSec, 0, null);
        }

        return new ScheduleStatus("done", "演示课程", "", "", "",
            0, 0, 0, 0, 0, classSec, 0, null);
    }

    // ————————————————————— 渲染（回显） —————————————————————

    void Render(DateTime simNow, ScheduleStatus? precomputed = null, bool single = false)
    {
        var status = precomputed
            ?? (_provider is not null ? _provider(simNow) : Ctx.Schedule.Engine.Compute(simNow, _advance.Value, _offset.Value));

        _capsuleText.Text = ScheduleText.Core(status);

        var accent = status.IsOngoing ? Color.FromRgb(0x30, 0xd1, 0x58)
                   : status.IsUpcoming ? Color.FromRgb(0x4d, 0xa3, 0xff)
                   : Color.FromRgb(0x8a, 0x8a, 0x8e);
        _capsule.BorderBrush = new SolidColorBrush(accent);
        _capsule.BorderThickness = new Thickness(0, 0, 0, 3);

        var countdown = status.IsUpcoming && status.UntilSec <= _countdown.Value;
        var stage = status.IsOngoing ? "上课中"
                  : status.IsUpcoming ? (countdown ? "倒计时" : "课间")
                  : status.Status == ScheduleEngine.StatusDone ? "已下课"
                  : status.Status == ScheduleEngine.StatusHoliday ? "假期中"
                  : "无课";

        var ratio = status.IsOngoing && status.DurationSec > 0
            ? 1.0 - (double)status.RemainSec / status.DurationSec
            : status.IsUpcoming
                ? 1.0 - Math.Clamp((double)status.UntilSec / Math.Max(1, Math.Max(_advance.Value * 60, _countdown.Value)), 0, 1)
                : 0.0;

        if (single || _simTotal <= 0)
        {
            _timeline.Value = 0;
        }
        else
        {
            var done = Math.Clamp((simNow - _simStart).TotalSeconds / Math.Max(1, _simTotal), 0, 1);
            _timeline.Value = done * 100;
        }

        var left = status.IsOngoing ? $"还剩 {status.RemainMin} 分钟"
                 : status.IsUpcoming ? $"距离上课 {status.UntilMin} 分钟"
                 : "—";

        _stage.Text = $"模拟时刻 {simNow:HH:mm:ss}   ·   {stage}   ·   {left}"
                    + (_simulated ? "   ·   模拟课堂（当前课表无课）" : "")
                    + (Ctx.DemoRunning ? "   ·   正在灵动岛上演示" : "");

        _detail.Text =
            $"状态：{status.Status}    节次：{(status.Index < 0 ? "—" : (status.Index + 1).ToString())}    " +
            $"课程：{(string.IsNullOrEmpty(status.Course) ? "—" : status.Course)}\n" +
            $"环颜色：#{accent.R:X2}{accent.G:X2}{accent.B:X2}    环比例：{ratio:P0}    " +
            $"倒计时态：{(countdown ? "是（胶囊宽 600）" : "否（胶囊宽 240 + 插件位）")}    " +
            $"倍速：{_speedValue:0.#}×";
    }

    void SaveParams()
    {
        Vm.ScheduleAdvanceMinutes = _advance.Value;
        Vm.ScheduleTimeOffsetSeconds = _offset.Value;
        Vm.IslandCountdownSec = _countdown.Value;
        Tell(this, "已写入 settings.json，灵动岛按新参数运行。", "状态测试");
    }
}
