namespace ClassDailyLand2.Plugin;

/// <summary>
/// 徽标/标签的语义色调。宿主按当前主题取色，插件不要自己传颜色 ——
/// 这样深色浅色两套主题下都好看，也不会和宿主的视觉语言打架。
/// </summary>
public enum BadgeTone
{
    /// <summary>中性（次要文字色）。</summary>
    Default = 0,
    /// <summary>强调蓝（默认强调色）。</summary>
    Accent = 1,
    /// <summary>成功/进行中（绿）。</summary>
    Success = 2,
    /// <summary>提醒（黄）。</summary>
    Warning = 3,
    /// <summary>警示（红）。</summary>
    Danger = 4,
}

/// <summary>胶囊状态字符串常量（与宿主 <c>ScheduleStatus.Status</c> 一致）。</summary>
public static class HostStatus
{
    public const string Upcoming = "upcoming";
    public const string Ongoing = "ongoing";
    public const string Holiday = "holiday";
    public const string None = "none";
    public const string Done = "done";
}

// ————————————————————— 灵动岛（主岛） —————————————————————

/// <summary>
/// 主岛文案补充位：非空时追加在核心文案后面（<c>正在上课 · 数学 · 你的文案</c>）。
/// 多个插件竞争同一个显示位，按 <see cref="Priority"/> 取最大，平级先注册者胜。
/// </summary>
/// <param name="Text">每秒在 UI 线程调用，必须够快（禁 IO / 网络 / 大计算），可为 null 表示本秒不显示。</param>
/// <param name="Priority">竞争权重：同一显示位取最大，平级先注册者胜。</param>
public sealed record IslandTextSpec(Func<string?> Text, int Priority = 0);

/// <summary>
/// 主岛加长段：胶囊宽度增加 <see cref="Width"/> 像素，给徽标或长插件文案留位。
/// 宽度是 DIP；与 <see cref="IslandBadgeSpec"/> 同时注册时，徽标宽度由宿主自动叠加。
/// </summary>
public sealed record IslandExtraSpec(int Width, int Priority = 0);

/// <summary>主岛右侧小徽标（画在预留宽度里，不覆盖文案）。</summary>
public sealed record IslandBadge(string Text, BadgeTone Tone = BadgeTone.Default);

/// <summary>主岛徽标位。</summary>
public sealed record IslandBadgeSpec(Func<IslandBadge?> Badge, int Priority = 0);

// ————————————————————— 副岛 —————————————————————

/// <summary>副岛内容：<see cref="Progress"/> 取 0..1，收起态会画成圆环里的进度点。</summary>
public sealed record SubIslandInfo(string Title, string Value, double Progress = 0);

/// <summary>副岛内容位（副岛平时收起成 40×40 的圆，点击展开成长条）。</summary>
public sealed record SubIslandSpec(Func<SubIslandInfo?> Content, int Priority = 0);

// ————————————————————— 下拉面板 —————————————————————

/// <summary>
/// 主岛点击后弹出的下拉面板。宿主会把所有插件面板与内置「今日课程」并排排开，
/// 总宽超过屏幕 80% 时从右往左丢弃。
/// </summary>
/// <param name="Title">面板标题（暂只用于日志与无障碍名称）。</param>
/// <param name="Width">面板宽度（DIP），典型 200~320。</param>
/// <param name="Lines">每秒在 UI 线程调用一次，返回逐行文本；null 表示这一秒不显示该面板。</param>
/// <param name="MaxHeightRatio">面板最大高度占工作区高度的比例，默认 1/3。</param>
/// <param name="Priority">竞争权重（仅在同位置只能显示一个时生效）。</param>
public sealed record PanelSpec(
    string Title,
    double Width,
    Func<IReadOnlyList<string>?> Lines,
    double MaxHeightRatio = 1.0 / 3.0,
    int Priority = 0);

// ————————————————————— 班级日常（主窗） —————————————————————

/// <summary>主窗追加区块：一个标题 + 若干行正文（等比作业那套排版，纯文字）。</summary>
public sealed record BoardSection(string Title, IReadOnlyList<string> Lines);

/// <summary>主窗区块位：追加在作业列表下面。</summary>
public sealed record BoardSectionSpec(Func<BoardSection?> Section, int Priority = 0);

/// <summary>主窗标题行右侧的小徽标（如「Wi-Fi 正常」「值日已排」）。</summary>
public sealed record BoardBadge(string Text, BadgeTone Tone = BadgeTone.Default);

/// <summary>主窗徽标位。</summary>
public sealed record BoardBadgeSpec(Func<BoardBadge?> Badge, int Priority = 0);

/// <summary>主窗右键菜单项。<see cref="Order"/> 越小越靠上，宿主默认项从 100 起。</summary>
public sealed record BoardActionSpec(string Text, Action Invoke, int Order = 50);

/// <summary>托盘右键菜单项（排在「设置」之后、「退出」之前）。</summary>
public sealed record TrayActionSpec(string Text, Action Invoke, int Order = 50);

// ————————————————————— 只读数据快照 —————————————————————

/// <summary>一节课（<see cref="Start"/>/<see cref="End"/> 为已叠加时间偏移的 HH:mm）。</summary>
public sealed record CourseEntry(int Index, string Course, string Start, string End, bool IsOngoing);

/// <summary>当前课表状态快照（每秒刷新，插件只读）。</summary>
public sealed record HostSnapshot(
    string Status,
    string StatusText,
    string Course,
    int Index,
    string Start,
    string End,
    int RemainMinutes,
    int UntilMinutes,
    string? HolidayName,
    DateTime Now);

/// <summary>值日与出勤快照。</summary>
public sealed record DutySnapshot(string TodayName, int ShouldCount, int ActualCount);

/// <summary>一条作业。</summary>
public sealed record HomeworkEntry(string Subject, string Content, string Date);

/// <summary>主屏工作区（DIP）。插件做自适应布局时用。</summary>
public sealed record HostWorkArea(double X, double Y, double Width, double Height, double Scale);

/// <summary>每秒 tick 事件参数。</summary>
public sealed class HostTickEventArgs : EventArgs
{
    public HostTickEventArgs(HostSnapshot snapshot) => Snapshot = snapshot;
    public HostSnapshot Snapshot { get; }
}

/// <summary>设置变更事件参数：给出被改动的键。</summary>
public sealed class HostSettingsEventArgs : EventArgs
{
    public HostSettingsEventArgs(IReadOnlyList<string> keys) => Keys = keys;
    public IReadOnlyList<string> Keys { get; }
}
