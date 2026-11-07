using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ClassDailyLand2.App.Integrations.Weather;

/// <summary>
/// 天气动画控件：按 <see cref="Kind"/> 切到对应分组并播它的循环动画。
///
/// <para>四处复用 —— 副岛收起态的圆里、副岛展开态、主窗插件区块、设置页动画预览
/// （预览页会同时挂十几个实例，所以**绝不能用静态字段记当前实例**，DP 回调必须拿 sender）。
/// 颜色一律走 <c>DynamicResource</c>（浅深主题各一套），动画可被 <see cref="Animated"/> 关掉；
/// 控件隐藏或被移出可视树时**必须停表**，否则 Storyboard 会一直持有已脱离的元素空转 ——
/// 副岛是常驻 topmost 窗，最容易踩这一条。</para>
/// </summary>
public partial class WeatherArt : UserControl
{
    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        WeatherArtKinds.Clear, WeatherArtKinds.Partly, WeatherArtKinds.Cloudy, WeatherArtKinds.Fog,
        WeatherArtKinds.Drizzle, WeatherArtKinds.Rain, WeatherArtKinds.Showers, WeatherArtKinds.Sleet,
        WeatherArtKinds.Snow, WeatherArtKinds.Thunder, WeatherArtKinds.Hail,
    };

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(WeatherArt),
        new PropertyMetadata(WeatherArtKinds.Cloudy, OnDepChanged));

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(
        nameof(IsDay), typeof(bool), typeof(WeatherArt),
        new PropertyMetadata(true, OnDepChanged));

    public static readonly DependencyProperty AnimatedProperty = DependencyProperty.Register(
        nameof(Animated), typeof(bool), typeof(WeatherArt),
        new PropertyMetadata(true, OnDepChanged));

    readonly List<Storyboard> _playing = new();

    /// <summary>天气种类，取值见 <see cref="WeatherArtKinds"/>。认不出的按「阴」画。</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>只影响晴天：白天画太阳，晚上画月牙。</summary>
    public bool IsDay
    {
        get => (bool)GetValue(IsDayProperty);
        set => SetValue(IsDayProperty, value);
    }

    /// <summary>关掉后只画静态图形（省电 / 用户关了动画）。</summary>
    public bool Animated
    {
        get => (bool)GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    public WeatherArt()
    {
        InitializeComponent();

        Loaded += (_, _) => Apply();
        IsVisibleChanged += (_, _) => Apply();
        // 被移出可视树（主窗区块整块重建）时必须停表
        Unloaded += (_, _) => StopAll();
    }

    static void OnDepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WeatherArt art) art.Apply();
    }

    /// <summary>把 Kind 规整成已知种类；未知的画成「阴」—— 宁可保守也不要开天窗。</summary>
    public static string Normalize(string? kind) =>
        kind is not null && Kinds.Contains(kind) ? kind : WeatherArtKinds.Cloudy;

    void Apply()
    {
        // DP 回调理论上不会早于 InitializeComponent，但静态资源/样式路径会，
        // 拿 null当哨兵挡一下比抛 NRE 好
        if (Stage is null) return;

        var kind = Normalize(Kind);
        var key = kind == WeatherArtKinds.Clear ? (IsDay ? "clear-day" : "clear-night") : kind;

        Show(G_ClearDay, key == "clear-day");
        Show(G_ClearNight, key == "clear-night");
        Show(G_Partly, key == "partly");
        Show(G_Cloudy, key == "cloudy");
        Show(G_Fog, key == "fog");
        Show(G_Drizzle, key == "drizzle");
        Show(G_Rain, key == "rain");
        Show(G_Showers, key == "showers");
        Show(G_Sleet, key == "sleet");
        Show(G_Snow, key == "snow");
        Show(G_Thunder, key == "thunder");
        Show(G_Hail, key == "hail");

        StopAll();
        if (!Animated || !IsVisible || !IsLoaded) return;

        // ★ key 带 "sb." 前缀，与 WeatherArt.xaml 里 x:Key="sb.<kind>" 对齐。
        //   不带前缀时 Resources[key] 恒为 null —— 分组照切、图标照画，唯独动画一个都不起
        //   （表现就是"图标是静态的"，很难一眼看出是查表失败）。
        if (Resources["sb." + key] is not Storyboard sb) return;

        sb.Begin(this, true);
        _playing.Add(sb);
    }

    static void Show(FrameworkElement group, bool show) =>
        group.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    void StopAll()
    {
        foreach (var sb in _playing)
        {
            try { sb.Stop(this); }
            catch { /* 元素已脱离 namescope，停不掉也不该影响界面 */ }
        }
        _playing.Clear();
    }
}
