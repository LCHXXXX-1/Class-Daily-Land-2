using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Integrations.Audio;

/// <summary>
/// 媒体展开卡：点岛上媒体片段后从岛下方浮出（封面 + 歌名/歌手 + 播放暂停）。
/// 6 秒无操作自动收起；鼠标停在卡上会一直续命。
/// </summary>
public sealed partial class MediaCardWindow : Window
{
    readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromSeconds(6) };
    bool _closing;

    /// <summary>淡出代次：Present 时 +1，过期的 Completed 回调据此自我作废。</summary>
    int _fadeGeneration;

    public event Action? PlayPauseRequested;

    public MediaCardWindow()
    {
        InitializeComponent();

        Left = 0;
        Top = -400;      // 动画前必须给具体值（NaN 会让 DoubleAnimation 抛异常）
        Card.Background = Palette.IslandBg;
        TitleText.Foreground = Palette.IslandText;
        ArtistText.Foreground = Palette.IslandText;

        PlayButton.Click += (_, _) => PlayPauseRequested?.Invoke();

        SourceInitialized += (_, _) => Interop.MakeToolWindowNoActivate(this);
        MouseEnter += (_, _) => _hold.Stop();
        MouseLeave += (_, _) => _hold.Start();

        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            Dismiss();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };

        // 外部直接 Hide()（更新前隐藏等）不走 Dismiss，_hold 会继续空跑 6 秒；
        // 只要不可见就停表，重新 Present 时会再 Start。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _hold.Stop();
        };
    }

    /// <summary>显示并更新内容；已显示时只换内容（不重新播入场动画）。</summary>
    public void Present(MediaDisplay media, double anchorRight, double anchorBottom)
    {
        Update(media);

        var work = WindowUtil.WorkAreaDip(this);
        var left = Math.Round(Math.Min(anchorRight - Width, work.Right - Width - 8));
        var top = Math.Round(anchorBottom + 6);
        Left = Math.Max(work.Left + 8, left);
        Top = top;

        // 上一次淡出还没播完就又来了：作废那一轮，否则它 140ms 后会把刚弹出的卡 Hide 掉
        _fadeGeneration++;

        if (!IsVisible)
        {
            _closing = false;
            Opacity = 0;
            Show();
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160)));
            BeginAnimation(OpacityProperty, fade);
            CardShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = Anim.Ease("EaseOutCubic"),
                });
        }

        _hold.Stop();
        _hold.Start();
    }

    public void Update(MediaDisplay media)
    {
        TitleText.Text = media.Title;
        ArtistText.Text = string.IsNullOrEmpty(media.Artist) ? "未知艺术家" : media.Artist;
        ArtImage.Source = media.Artwork;
        ArtFrame.Visibility = media.Artwork is null ? Visibility.Collapsed : Visibility.Visible;

        // E768 = ▶ / E769 = ⏸
        PlayGlyph.Text = media.Playing ? "\uE769" : "\uE768";
        PlayButton.ToolTip = media.Playing ? "暂停" : "播放";
    }

    public void Dismiss()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        _hold.Stop();

        var generation = ++_fadeGeneration;
        var fade = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(140)));
        fade.Completed += (_, _) =>
        {
            if (generation != _fadeGeneration) return;    // 期间又 Present 过，这次淡出作废
            Hide();
            Opacity = 0;
            _closing = false;
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
