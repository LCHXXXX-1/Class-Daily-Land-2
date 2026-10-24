using ClassDailyLand.Plugin;

namespace HelloPlugin;

/// <summary>
/// 示例插件：把契约里最有代表性的几个显示位都用一遍，可以当模板直接改。
///
/// 用到的能力：
///   · 元信息与日志   —— PluginId / DataDirectory / Log
///   · 私有存储       —— ReadState / WriteState / FlushState
///   · 主岛           —— RegisterIslandText / RegisterIslandBadge
///   · 副岛           —— RegisterSubIsland
///   · 下拉面板       —— RegisterPanel（读 Snapshot / CoursesOfDay）
///   · 班级日常       —— RegisterBoardBadge / RegisterBoardSection / RegisterBoardAction
///   · 托盘           —— RegisterTrayAction
///   · 宿主动作       —— Toast / OpenSettings
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    IPluginHost? _host;
    int _launches = 1;

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // 启动次数存在插件自己的 data/state.json 里，用来验证"私有存储"这条链路
        _launches = int.TryParse(host.ReadState("launches"), out var n) ? n + 1 : 1;
        host.WriteState("launches", _launches.ToString());
        host.FlushState();

        host.Log($"示例插件已启动（第 {_launches} 次），数据目录：{host.DataDirectory}");

        // ——— 主岛：文案后缀 + 右侧徽标 ———
        // 回调每秒都可能被调用，这里只读字段，绝不做 IO
        host.RegisterIslandText(new IslandTextSpec(
            () => _launches > 1 ? $"示例×{_launches}" : "示例", Priority: 0));

        host.RegisterIslandBadge(new IslandBadgeSpec(
            () => new IslandBadge("DEMO", BadgeTone.Accent)));

        // ——— 副岛 ———
        host.RegisterSubIsland(new SubIslandSpec(
            () => new SubIslandInfo("示例", $"第 {_launches} 次", 0.5)));

        // ——— 下拉面板：读宿主数据 ———
        host.RegisterPanel(new PanelSpec("示例面板", 210, () =>
        {
            var snap = host.Snapshot;
            var courses = host.CoursesOfDay(host.Now);
            return new List<string>
            {
                "示例插件",
                "",
                snap.Status switch
                {
                    HostStatus.Ongoing => $"正在上 · {snap.Course}",
                    HostStatus.Upcoming => $"下节 · {snap.Course}",
                    HostStatus.Holiday => $"假期 · {snap.HolidayName}",
                    HostStatus.Done => "今天已放学",
                    _ => "今天没有课",
                },
                $"今日 {courses.Count} 节",
                _launches > 1 ? $"启动 {_launches} 次" : "首次启动",
            };
        }));

        // ——— 班级日常：标题徽标 + 区块 + 右键菜单 ———
        host.RegisterBoardBadge(new BoardBadgeSpec(
            () => new BoardBadge("DEMO", BadgeTone.Success)));

        host.RegisterBoardSection(new BoardSectionSpec(() =>
        {
            var duty = host.Duty;
            var name = string.IsNullOrWhiteSpace(duty.TodayName) ? "未排" : duty.TodayName;
            return new BoardSection("示例区块", new[]
            {
                $"值日：{name}",
                $"应到 {duty.ShouldCount} 人 · 实到 {duty.ActualCount} 人",
            });
        }));

        host.RegisterBoardAction(new BoardActionSpec(
            "示例：弹个提示", () => host.Toast("示例插件", "这是插件注册的右键菜单项"), Order: 10));

        // ——— 托盘菜单 ———
        host.RegisterTrayAction(new TrayActionSpec(
            "示例：打开插件页", () => host.OpenSettings("plugins"), Order: 10));

        // ——— 事件：设置变化时记一句，证明事件链路是通的 ———
        host.SettingsChanged += (_, e) =>
            host.Log("设置变了：" + string.Join(", ", e.Keys));
    }

    public void Shutdown()
    {
        _host?.Log("示例插件已卸载");
        _host = null;
    }
}
