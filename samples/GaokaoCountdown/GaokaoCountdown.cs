using ClassDailyLand2.Plugin;
using System.Globalization;

namespace GaokaoCountdown;

/// <summary>
/// 高考倒计时插件：在灵动岛下拉面板里显示距离高考还有多少天，并可自行配置「届别」。
///
/// <para><b>设置从哪来</b>：插件在 <see cref="Initialize"/> 里用
/// <c>host.RegisterSettings</c> 向宿主**声明**自己的设置页（届别 + 是否上主岛）。
/// 宿主按声明渲染原生控件，「设置 → 插件 → 已安装」里就会出现「设置」按钮；
/// 插件本身不碰任何 WPF 类型，值的落点就是自己的 <c>data/state.json</c>。</para>
///
/// <para><b>届别语义</b>：2029 届 = 2029 年 6 月 7 日高考（考试当天按 9:00 开考算小时数）。
/// 默认值是「下一个 6 月 7 日」所在年份。</para>
///
/// <para><b>回调纪律</b>：面板每秒在 UI 线程求值一次，里面只有日期减法与几个字符串拼接。</para>
/// </summary>
public sealed class GaokaoPlugin : IPlugin
{
    /// <summary>考试当天上午开考时间，用来算"还有几小时"。</summary>
    static readonly TimeSpan ExamStart = TimeSpan.FromHours(9);

    IPluginHost? _host;

    /// <summary>届别 = 高考年份（2029 届 → 2029）。</summary>
    int _grade = DateTime.Today.Year;

    /// <summary>是否在灵动岛文案位也显示天数。</summary>
    bool _showOnIsland;

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // ★ 向宿主要求一页设置。声明的顺序就是界面从上到下的顺序。
        host.RegisterSettings(new SettingsPageSpec(
            Title: "高考倒计时",
            Fields: new[]
            {
                new SettingField(
                    Key: "grade",
                    Label: "届别",
                    Kind: SettingFieldKind.Choice,
                    Hint: "2029 届 = 2029 年 6 月 7 日高考（当天按 9:00 开考算小时数）。",
                    Options: BuildGradeOptions(host.Now.Year)),
                new SettingField(
                    Key: "show_on_island",
                    Label: "也在主岛显示",
                    Kind: SettingFieldKind.Toggle,
                    Hint: "开启后灵动岛文案位多一句「高考 N 天」；与其它插件同时注册时按优先级让位。",
                    DefaultValue: "false"),
            },
            Summary: Summary));

        // 宿主界面改完设置会立刻发这个事件，不必等接下来那次 Tick
        host.SettingChanged += (_, _) => Reload(host);

        Reload(host);

        host.RegisterPanel(new PanelSpec("高考倒计时", 240, BuildLines));

        host.RegisterIslandText(new IslandTextSpec(
            () => _showOnIsland ? $"高考 {DaysLeft(host)} 天" : null,
            Priority: 0));

        host.RegisterTrayAction(new TrayActionSpec(
            "高考倒计时 · 设置届别",
            () => host.OpenSettings("plugins"),
            Order: 60));

        host.Log($"已启动：{_grade} 届（{ExamDate(_grade):yyyy-MM-dd}，还有 {DaysLeft(host)} 天）" +
                 (_showOnIsland ? "，主岛也显示" : ""));
    }

    public void Shutdown()
    {
        _host?.Log("正在卸载");
        _host = null;
    }

    // ————————————————————— 设置读写 —————————————————————

    /// <summary>把 state.json 里的值读进内存字段。设置项改动后由 SettingChanged 调用。</summary>
    void Reload(IPluginHost host)
    {
        var gradeText = host.ReadState("grade");
        _grade = int.TryParse(gradeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 2100
            ? year
            : DefaultGrade(host.Now);

        var onIsland = host.ReadState("show_on_island");
        _showOnIsland = string.Equals(onIsland, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>已安装列表里那行小字。</summary>
    string? Summary()
    {
        var host = _host;
        if (host is null) return null;
        var days = DaysLeft(host);
        return days > 0 ? $"{_grade} 届 · 还有 {days} 天"
             : days == 0 ? $"{_grade} 届 · 就是今天"
             : $"{_grade} 届 · 已结束 {-days} 天";
    }

    // ————————————————————— 面板内容 —————————————————————

    /// <summary>每秒调用一次，必须够快。返回 null 表示这一秒不显示面板。</summary>
    IReadOnlyList<string>? BuildLines()
    {
        var host = _host;
        if (host is null) return null;

        // 一律用 host.Now：它已叠加设置里的时间偏移，和界面上显示的时间一致
        var now = host.Now;
        var days = DaysLeft(host);

        var lines = new List<string>(6) { $"{_grade} 届高考" };

        if (days > 0)
        {
            lines.Add($"还有 {days} 天");

            // 近处给小时、中段给周、远处给月 —— "22752 小时"这种数字没有意义
            if (days <= 7)
            {
                var left = ExamDate(_grade) + ExamStart - now;
                if (left > TimeSpan.Zero) lines.Add($"约 {(int)left.TotalHours} 小时");
            }
            else if (days <= 90)
            {
                lines.Add($"约 {days / 7} 周");
            }
            else
            {
                lines.Add($"约 {days / 30} 个月");
            }
        }
        else if (days == 0)
        {
            lines.Add("就是今天 · 加油");
        }
        else
        {
            lines.Add($"已结束 {-days} 天");
        }

        lines.Add("");                                   // 空行 = 段落间隔
        lines.Add($"{now:M月d日} · {WeekName(now.DayOfWeek)}");
        lines.Add($"目标 {ExamDate(_grade):yyyy-MM-dd}");

        return lines;
    }

    // ————————————————————— 日期计算 —————————————————————

    static DateTime ExamDate(int grade) => new(grade, 6, 7);

    /// <summary>距离本实例设定届别的高考还有几天（按 app 内"今天"算）。</summary>
    int DaysLeft(IPluginHost host) => (ExamDate(_grade).Date - host.Now.Date).Days;

    /// <summary>下一个 6 月 7 日所在年份：今天还没到 6/7 就是今年，否则明年。</summary>
    static int DefaultGrade(DateTime now) =>
        now.Date <= new DateTime(now.Year, 6, 7) ? now.Year : now.Year + 1;

    /// <summary>届别下拉：前一年到后八年（往届也留一个，免得转学生没得选）。</summary>
    static IReadOnlyList<SettingOption> BuildGradeOptions(int currentYear)
    {
        var options = new List<SettingOption>(10);
        for (var year = currentYear - 1; year <= currentYear + 8; year++)
            options.Add(new SettingOption(year.ToString(CultureInfo.InvariantCulture), $"{year} 届"));
        return options;
    }

    static string WeekName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
