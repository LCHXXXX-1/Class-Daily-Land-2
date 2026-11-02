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

// ————————————————————— 插件自己的设置页 —————————————————————

/// <summary>设置项类型。宿主按它决定渲染成什么控件。</summary>
public enum SettingFieldKind
{
    /// <summary>单行文本（<c>TextBox</c>）。</summary>
    Text = 0,
    /// <summary>数字（带 ▲▼ 的 <c>NumberBox</c>），配合 <see cref="SettingField.Min"/> / <see cref="SettingField.Max"/>。</summary>
    Number = 1,
    /// <summary>开关（<c>FluentToggle</c>），值存 <c>"true"</c> / <c>"false"</c>。</summary>
    Toggle = 2,
    /// <summary>单选下拉（<c>ComboBox</c>），选项见 <see cref="SettingField.Options"/>。</summary>
    Choice = 3,
}

/// <summary>下拉选项：<see cref="Value"/> 是落盘值，<see cref="Label"/> 是显示文案。</summary>
public sealed record SettingOption(string Value, string Label);

/// <summary>
/// 一个设置项。<see cref="Key"/> 同时是 <c>ReadState</c> / <c>WriteState</c> 的键 ——
/// 宿主写完会触发 <see cref="IPluginHost.SettingChanged"/>，插件据此刷新自己的缓存。
/// </summary>
/// <param name="Key">落盘键名（插件私有 state.json）。建议用 snake_case。</param>
/// <param name="Label">左侧标题。</param>
/// <param name="Kind">控件类型。</param>
/// <param name="Hint">标题下面那行灰色说明，可为 null。</param>
/// <param name="DefaultValue">没写过值时界面显示的默认值；不写则文本空、数字 0、开关关、下拉选第一项。</param>
/// <param name="Options">仅 <see cref="SettingFieldKind.Choice"/> 用。</param>
/// <param name="Min">仅 Number 用，最小值（0 与 Max 都为 0 时表示不限）。</param>
/// <param name="Max">仅 Number 用，最大值。</param>
/// <param name="Unit">仅 Number 用，单位后缀（如 " 天"）。</param>
public sealed record SettingField(
    string Key,
    string Label,
    SettingFieldKind Kind,
    string? Hint = null,
    string? DefaultValue = null,
    IReadOnlyList<SettingOption>? Options = null,
    double Min = 0,
    double Max = 0,
    string Unit = "");

/// <summary>
/// 插件的设置页。插件在 <c>Initialize</c> 里通过
/// <see cref="IPluginHost.RegisterSettings"/> 声明，宿主会在「设置 → 插件」的
/// 已安装列表里给这个插件亮出「设置」按钮，点开按 <see cref="Fields"/> 逐项渲染。
///
/// <para>声明式而不是"插件自己画界面"：插件跑在独立 ALC 里、不许碰 WPF 视觉树，
/// 宿主保证深色/浅色主题、触屏放大、键盘操作全都一致。</para>
/// </summary>
/// <param name="Title">设置页标题（通常是插件名）。</param>
/// <param name="Fields">设置项，按顺序渲染。</param>
/// <param name="Summary">已安装列表里那行小字（如「2029 届 · 还有 218 天」）；返回 null 表示不显示。</param>
public sealed record SettingsPageSpec(
    string Title,
    IReadOnlyList<SettingField> Fields,
    Func<string?>? Summary = null);

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

/// <summary>插件自己的设置项被改动（宿主界面写的，不是插件自己写的）。</summary>
public sealed class PluginSettingChangedEventArgs : EventArgs
{
    public PluginSettingChangedEventArgs(string key, string? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>被改动的键（<see cref="SettingField.Key"/>）。</summary>
    public string Key { get; }

    /// <summary>新值；null 表示这一项被清空。</summary>
    public string? Value { get; }
}
