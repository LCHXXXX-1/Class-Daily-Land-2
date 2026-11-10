using ClassDailyLand2.App.DeveloperTool;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// DeveloperTool 在线安装器的下载进度窗：实时百分比 + 区间速度，下完自动拉起安装器。
/// 打开即开始下载；取消 / 失败后停在原地，可以「重试」。
/// </summary>
public partial class DeveloperToolSetupWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _downloading;
    private bool _launched;

    // 实时下载速度：每 300ms 算一次区间速度（与在线安装器同口径，掉速/卡住能立刻看出来）
    private readonly Stopwatch _speedWatch = new();
    private long _speedLastBytes;
    private double _speedLastSeconds;

    public DeveloperToolSetupWindow()
    {
        InitializeComponent();

        // 提示框统一入场：从下往上滑上来 + 淡入（只动内容，不挪窗口）
        Loaded += (_, _) => UiMotion.SlideUpIn(Content as FrameworkElement ?? this, maxDistance: 180);

        Loaded += (_, _) => StartDownload();
    }

    private async void StartDownload()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _downloading = true;
        _launched = false;
        CloseButton.Content = "取消";
        RetryButton.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        ResetSpeed();
        SetStatus("正在连接下载服务器…", error: false);

        // Progress<T> 会回到创建它的线程（UI 线程）上回调，所以这里直接动控件是安全的
        var progress = new Progress<SetupDownloadProgress>(report =>
        {
            if (report.Total > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Clamp(report.Percent, 0, 100);
                SizeText.Text = $"{report.Received / 1048576.0:0.0} / {report.Total / 1048576.0:0.0} MB";
            }
            else
            {
                Progress.IsIndeterminate = true;
                SizeText.Text = $"{report.Received / 1048576.0:0.0} MB";
            }

            UpdateSpeed(report.Received);
            if (!string.IsNullOrWhiteSpace(report.Message)) SetStatus(report.Message!, error: false);
        });

        try
        {
            var setupPath = await DeveloperToolService.DownloadSetupAsync(progress, token);

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            SpeedText.Text = "0.0 MB/s";

            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,   // 安装器自己带清单，该提权时由它弹 UAC
            })?.Dispose();

            _downloading = false;
            _launched = true;
            CloseButton.Content = "完成";
            SetStatus("安装程序已启动，请按向导完成安装；装好后回到这里再点「前往」即可打开。", error: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _downloading = false;
            SpeedText.Text = "0.0 MB/s";
            Progress.IsIndeterminate = false;
            SetStatus("已取消下载。", error: false);
            RetryButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _downloading = false;
            SpeedText.Text = "0.0 MB/s";
            Progress.IsIndeterminate = false;
            SetStatus("下载失败：" + ex.Message, error: true);
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>区间速度：最近 300ms 收了多少字节（不是平均值）。</summary>
    private void UpdateSpeed(long received)
    {
        var now = _speedWatch.Elapsed.TotalSeconds;
        var window = now - _speedLastSeconds;
        var delta = received - _speedLastBytes;

        if (window < 0.3 || delta < 0) return;

        SpeedText.Text = $"{(delta / window) / 1024 / 1024:F1} MB/s";
        _speedLastBytes = received;
        _speedLastSeconds = now;
    }

    private void ResetSpeed()
    {
        _speedWatch.Restart();
        _speedLastBytes = 0;
        _speedLastSeconds = 0;
        SpeedText.Text = "0.0 MB/s";
        SizeText.Text = "0.0 MB";
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? ErrorBrush
            : (Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private static readonly Brush ErrorBrush = CreateErrorBrush();

    private static Brush CreateErrorBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));   // 与更新提示窗的强制红一致
        brush.Freeze();
        return brush;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_downloading)
        {
            Close();
            return;
        }

        // 下载中点「取消」= 取消任务，不关窗：半截 .part 由服务层清掉，界面留在原地可重试
        _cts?.Cancel();
        SetStatus("正在取消…", error: false);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        StartDownload();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关窗口 = 取消：下一个读取检查点退出；安装器已经拉起来时直接放行
        if (!_launched) _cts?.Cancel();
        base.OnClosing(e);
    }
}
