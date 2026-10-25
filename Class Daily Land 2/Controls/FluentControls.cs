using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassDailyLand.App.Controls;

// ============================================================================
//  设置中心用的 Fluent 风格控件（外观全部在 Themes/Controls.xaml 里定义模板）
//  对位 ui/lib/controls.js 的 numberBox / selectorBar / settingRow / linkRow /
//  infoBar / badge / cardGroup / card。
// ============================================================================

/// <summary>数字框：输入 + ▲▼ 微调 + 单位后缀，失焦或回车时按 min/max/step 夹取。</summary>
public class NumberBox : Control
{
    static NumberBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NumberBox),
            new FrameworkPropertyMetadata(typeof(NumberBox)));
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MinValue));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MaxValue));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumberBox), new PropertyMetadata(1));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumberBox), new PropertyMetadata(""));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(NumberBox), new PropertyMetadata("数值"));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>值真正发生变化（用户操作）后触发。</summary>
    public event Action<int>? Committed;

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumberBox)d;
        box._suppress = true;
        if (box._input is not null && box._input.Text != ((int)e.NewValue).ToString())
            box._input.Text = ((int)e.NewValue).ToString();
        box._suppress = false;
    }

    TextBox? _input;
    bool _suppress;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _input = GetTemplateChild("PART_Input") as TextBox;
        if (_input is null) return;

        _input.Text = Value.ToString();
        _input.LostFocus += (_, _) => CommitFromText();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { CommitFromText(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Up) { Nudge(Step); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Down) { Nudge(-Step); e.Handled = true; }
        };

        if (GetTemplateChild("PART_Up") is RepeatButton up) up.Click += (_, _) => Nudge(Step);
        if (GetTemplateChild("PART_Down") is RepeatButton down) down.Click += (_, _) => Nudge(-Step);
    }

    void CommitFromText()
    {
        if (_input is null) return;
        var raw = _input.Text.Trim();
        var fallback = Value;
        var parsed = int.TryParse(raw, out var n) ? n : fallback;
        var clamped = Snap(parsed);
        _input.Text = clamped.ToString();
        if (clamped == Value && !_suppress) return;
        Value = clamped;
        Committed?.Invoke(clamped);
    }

    void Nudge(int delta)
    {
        var next = Snap(Value + delta);
        if (next == Value) return;
        Value = next;
        if (_input is not null) _input.Text = next.ToString();
        Committed?.Invoke(next);
    }

    int Snap(int v)
    {
        var step = Math.Max(1, Step);
        v = Math.Min(Maximum, Math.Max(Minimum, v));
        // 对齐到 step 网格（以 Minimum 为原点），再夹一次
        var origin = Minimum == int.MinValue ? 0 : Minimum;
        v = origin + (int)Math.Round((v - origin) / (double)step) * step;
        return Math.Min(Maximum, Math.Max(Minimum, v));
    }
}

/// <summary>分段选择器的一项（对位 combo options 的 {value,label}）。</summary>
public sealed class SelectorItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    internal SelectorBar? Owner { get; set; }

    /// <summary>是否被所属 SelectorBar 选中（模板里的 DataTrigger 依赖它）。</summary>
    public bool IsSelected => Owner is not null && Owner.SelectedValue == Value;

    public SelectorItem() { }
    public SelectorItem(string value, string label) { Value = value; Label = label; }

    internal void RaiseSelected() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 分段控件（WinUI SelectorBar）：一排互斥选项。
/// 对位 .win-selectorbar：外层 3px 内边距 + 9px 圆角淡底，选中项强调色底 + 反色文字。
/// </summary>
public class SelectorBar : ItemsControl
{
    static SelectorBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SelectorBar),
            new FrameworkPropertyMetadata(typeof(SelectorBar)));
        SelectedValueProperty = DependencyProperty.Register(
            nameof(SelectedValue), typeof(string), typeof(SelectorBar),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedValueChanged));
    }

    public static readonly DependencyProperty SelectedValueProperty;

    public string SelectedValue { get => (string)GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }

    /// <summary>用户点选后触发（外部写值不会触发）。</summary>
    public event Action<string>? SelectedChanged;

    /// <summary>供 ItemTemplate 里的按钮绑定（参数是 SelectorItem）。</summary>
    public ICommand SelectCommand { get; }

    public SelectorBar()
    {
        SelectCommand = new RelayCommand<SelectorItem>(item =>
        {
            if (item is null || SelectedValue == item.Value) return;
            SelectedValue = item.Value;
            SelectedChanged?.Invoke(item.Value);
        });
    }

    static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SelectorBar)d).RaiseSelected();

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        Hook();
    }

    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        base.OnItemsChanged(args);
        Dispatcher.BeginInvoke(Hook);   // 直接在 XAML 里写子项时，项还没进容器
    }

    void Hook()
    {
        foreach (var item in Items.OfType<SelectorItem>()) item.Owner = this;
        RaiseSelected();
    }

    void RaiseSelected()
    {
        foreach (var item in Items.OfType<SelectorItem>()) item.RaiseSelected();
    }
}

/// <summary>设置行：左侧标题 + 说明，右侧控件（对位 settingRow）。</summary>
public class SettingRow : ContentControl
{
    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow),
            new FrameworkPropertyMetadata(typeof(SettingRow)));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    /// <summary>禁用态（灰字 + 控件不可交互）。</summary>
    public static readonly DependencyProperty IsDisabledProperty = DependencyProperty.Register(
        nameof(IsDisabled), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public bool IsDisabled { get => (bool)GetValue(IsDisabledProperty); set => SetValue(IsDisabledProperty, value); }
}

/// <summary>链接行：彩色磁贴图标 + 标题/说明 + 右侧箭头，整行可点（对位 linkRow）。</summary>
public class LinkRow : ButtonBase
{
    static LinkRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LinkRow),
            new FrameworkPropertyMetadata(typeof(LinkRow)));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(LinkRow), new PropertyMetadata(""));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(LinkRow), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(LinkRow), new PropertyMetadata(null));

    /// <summary>磁贴颜色（底为该色 15% 透明、图标为该色，对位 --tile-color）。</summary>
    public static readonly DependencyProperty TileColorProperty = DependencyProperty.Register(
        nameof(TileColor), typeof(Brush), typeof(LinkRow), new PropertyMetadata(null));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush? TileColor { get => (Brush?)GetValue(TileColorProperty); set => SetValue(TileColorProperty, value); }
}

public enum InfoBarSeverity { Information, Success, Warning, Error }

/// <summary>提示条（对位 infoBar，也用作页面顶部 toast）。</summary>
public class InfoBar : Control
{
    static InfoBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoBar),
            new FrameworkPropertyMetadata(typeof(InfoBar)));
    }

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(InfoBarSeverity), typeof(InfoBar), new PropertyMetadata(InfoBarSeverity.Information));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata(""));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata(""));

    public static readonly DependencyProperty IsClosableProperty = DependencyProperty.Register(
        nameof(IsClosable), typeof(bool), typeof(InfoBar), new PropertyMetadata(true));

    public InfoBarSeverity Severity { get => (InfoBarSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool IsClosable { get => (bool)GetValue(IsClosableProperty); set => SetValue(IsClosableProperty, value); }

    /// <summary>点关闭钮后触发（模板里的 PART_Close）。</summary>
    public event Action? Closed;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is Button close)
            close.Click += (_, _) =>
            {
                Visibility = Visibility.Collapsed;
                Closed?.Invoke();
            };
    }
}

public enum BadgeVariant { Neutral, Accent, Ok, Warn, Bad }

/// <summary>小徽标（对位 badge：状态点 + 文字）。</summary>
public class Badge : Control
{
    static Badge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Badge),
            new FrameworkPropertyMetadata(typeof(Badge)));
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Badge), new PropertyMetadata(""));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BadgeVariant), typeof(Badge), new PropertyMetadata(BadgeVariant.Neutral));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public BadgeVariant Variant { get => (BadgeVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
}

/// <summary>卡片：12px 圆角 + 描边 + 内高光 + 静置阴影，内部纵向排子项。</summary>
public class Card : ItemsControl
{
    static Card()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card),
            new FrameworkPropertyMetadata(typeof(Card)));
    }

    // Padding 直接用 Control 的继承属性（模板里 TemplateBinding Padding）
}

/// <summary>
/// 分区：彩色磁贴标题 + 卡片 + 脚注（对位 app.js 的 section()）。
/// 它是 ItemsControl：行直接当子项写，这样每个容器拿得到 AlternationIndex，
/// 「第一行不画顶线、其余画分隔线」才成立（行套在 StackPanel 里就取不到索引）。
/// </summary>
public class SettingGroup : ItemsControl
{
    static SettingGroup()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingGroup),
            new FrameworkPropertyMetadata(typeof(SettingGroup)));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(SettingGroup), new PropertyMetadata(null));

    public static readonly DependencyProperty TileColorProperty = DependencyProperty.Register(
        nameof(TileColor), typeof(Brush), typeof(SettingGroup), new PropertyMetadata(null));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    /// <summary>是否作为大分区（上边距更大，对位 .group-title.is-section）。</summary>
    public static readonly DependencyProperty IsSectionProperty = DependencyProperty.Register(
        nameof(IsSection), typeof(bool), typeof(SettingGroup), new PropertyMetadata(false));

    /// <summary>卡片内容区的内边距（内嵌编辑器这类非行内容才需要）。</summary>
    public static readonly DependencyProperty CardPaddingProperty = DependencyProperty.Register(
        nameof(CardPadding), typeof(Thickness), typeof(SettingGroup), new PropertyMetadata(new Thickness()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush? TileColor { get => (Brush?)GetValue(TileColorProperty); set => SetValue(TileColorProperty, value); }
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public bool IsSection { get => (bool)GetValue(IsSectionProperty); set => SetValue(IsSectionProperty, value); }
    public Thickness CardPadding { get => (Thickness)GetValue(CardPaddingProperty); set => SetValue(CardPaddingProperty, value); }
}

/// <summary>
/// 描边图标：把 24×24 网格的 Geometry 等比缩放到 Size。
/// 不直接用 Viewbox 包 Path —— Viewbox 会按几何包围盒贴合，各图标实际边界不同，
/// 同一 Size 下视觉粗细就不一致了；这里固定一个 24×24 的画布再缩放。
/// </summary>
public class Icon : Control
{
    static Icon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon),
            new FrameworkPropertyMetadata(typeof(Icon)));
    }

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(20.0));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(1.5));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
}

/// <summary>开关（对位 win-toggle：40×20 轨道 + 12→14 圆钮）。</summary>
public class FluentToggle : CheckBox
{
    static FluentToggle()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FluentToggle),
            new FrameworkPropertyMetadata(typeof(FluentToggle)));
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FluentToggle), new PropertyMetadata("开关"));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
}
