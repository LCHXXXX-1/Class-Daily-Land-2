using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Installer.Online.Services;
using ClassDailyLand2.UpdateContract;
using Microsoft.Win32;

namespace ClassDailyLand2.Installer.Online;

public partial class MainWindow : Window
{
    private const string ProductFolderName = "Class Daily Land 2";

    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _installTarget = "";
    private string _appExe = "";

    // 实时下载速度：每 300ms 算一次区间速度
    private readonly Stopwatch _speedWatch = new();
    private long _speedLastBytes;
    private double _speedLastSeconds;

    public MainWindow()
    {
        InitializeComponent();

        InstallPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductFolderName);

        SetStatus("准备就绪。点击“安装”开始。", error: false);
    }

    // ————————————————————— 交互 —————————————————————

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = Directory.Exists(InstallPath.Text) ? InstallPath.Text : "",
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            InstallPath.Text = EnsureProductFolder(dialog.FolderName.Trim());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            Close();
            return;
        }

        _cts?.Cancel();
        SetStatus("正在取消…", error: false);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!TryResolveTarget(out var target)) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetBusy(true);
        ResetSpeed();

        // Progress<T> 会回到创建它的线程（UI 线程）上回调，所以这里直接动控件是安全的
        var progress = new Progress<DownloadProgress>(report =>
        {
            Progress.Value = Math.Clamp(report.Percent, 0, 100);
            if (!string.IsNullOrWhiteSpace(report.Message)) SetStatus(report.Message!, error: false);

            UpdateSpeed(report.Received);
        });

        var zipPath = Path.Combine(Path.GetTempPath(), "ClassDailyLand2-app.zip");
        var runtimePath = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-10-win-x64.exe");

        try
        {
            // ① 运行环境
            SetStatus("检查 .NET 10 桌面运行时…", error: false);

            var runtimeInstalled = false;
            if (!RuntimeChecker.HasDesktopRuntime(out var runtimeDetail))
            {
                SetStatus("缺少 .NET 10 桌面运行时，正在下载（约 60 MB）…", error: false);
                await DownloadService.DownloadAsync(SetupEndpoints.RuntimeUrls, runtimePath,
                    progress, 0, 45, token);

                SetStatus("正在安装 .NET 10 桌面运行时（会弹出系统授权提示）…", error: false);
                await RuntimeChecker.InstallAsync(runtimePath, token);

                if (!RuntimeChecker.HasDesktopRuntime(out runtimeDetail))
                    throw new InstallFailedException("运行库安装完成后仍然检测不到：" + runtimeDetail);

                runtimeInstalled = true;
                SetStatus("运行库已就绪。", error: false);
            }

            TryDelete(runtimePath);

            Directory.CreateDirectory(target);

            // ② 远端版本信息（唯一源：Gitee raw）：读 stable 通道的 version / download_url / sha256
            SetStatus("正在获取最新版本…", error: false);

            var release = await ReleaseFeed.FetchAsync(token);

            SetStatus($"正在下载 Class Daily Land 2 {release.Version}…", error: false);

            await DownloadService.DownloadAsync(new[] { release.DownloadUrl }, zipPath, progress,
                runtimeInstalled ? 45 : 2, 88, token);

            // ③ 校验：version.json 里带了 sha256 就一定验（宁可不装，也不能装错包）
            if (!string.IsNullOrEmpty(release.Sha256))
            {
                SetStatus("正在校验安装包…", error: false);

                var actual = await Task.Run(() => Hashing.ComputeFileSha256(zipPath), token);

                if (!Hashing.Sha256Equals(release.Sha256, actual))
                {
                    TryDelete(zipPath);
                    throw new InstallFailedException(
                        $"安装包校验失败（期望 {ShortHash(release.Sha256)}，实际 {ShortHash(actual)}），已删除下载文件");
                }
            }

            // ④ 解压
            SpeedText.Text = "0.0 MB/s";
            SetStatus("解压到 " + target + " …", error: false);
            await ZipInstaller.ExtractAsync(zipPath, target, progress, 90, 99, token);

            // ⑤ 快捷方式 + 卸载信息
            SetStatus("创建快捷方式与卸载信息…", error: false);
            var appExe = Path.Combine(target, SetupEndpoints.AppExeName);
            ShortcutService.CreateAll(target, appExe);
            UninstallRegistration.Register(target);

            // ⑥ 开机自启：写 HKCU\...\Run，值指向**主程序**（不是安装器自己）
            var autoStartFailed = false;
            if (AutoStartCheckBox.IsChecked == true && !StartupManager.SetStartup(true, appExe))
                autoStartFailed = true;

            TryDelete(zipPath);

            Progress.Value = 100;
            SpeedText.Text = "0.0 MB/s";
            SetStatus(autoStartFailed
                    ? "安装完成（开机自启写入失败，可在主程序「设置 → 通用」里重新开启）。"
                    : "安装完成。",
                error: autoStartFailed);
            SetBusy(false, finished: true);

            // ⑦ 不再直接启动：切到完成页，等待用户确认
            _installTarget = target;
            _appExe = appExe;
            ShowFinishPanel(autoStartFailed);
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SpeedText.Text = "0.0 MB/s";
            SetStatus("已取消，未完成的文件保留在安装目录里，可以直接重试。", error: false);
            SetBusy(false);
        }
        catch (Exception ex)
        {
            SpeedText.Text = "0.0 MB/s";
            // 任何一步失败都停在原地：显示原因，按钮恢复可用，用户改完可以重试。
            // 已知失败（连不上服务器 / 校验失败 …）直接用它自己的人话消息，不再套一层"安装失败："。
            SetStatus(ex is InstallFailedException ? ex.Message : "安装失败：" + ex.Message, error: true);
            SetBusy(false);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ————————————————————— 小工具 —————————————————————

    /// <summary>区间速度：最近 300ms 收了多少字节（不是平均值，掉速/卡住能立刻看出来）。</summary>
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
    }

    /// <summary>校验并规整安装路径；不合法时直接给红字提示并返回 false。</summary>
    private bool TryResolveTarget(out string target)
    {
        target = "";

        var raw = (InstallPath.Text ?? "").Trim().Trim('"');
        if (raw.Length == 0)
        {
            SetStatus("请先填写安装位置。", error: true);
            return false;
        }

        try
        {
            target = Path.GetFullPath(raw);
        }
        catch (Exception ex)
        {
            SetStatus("安装位置不合法：" + ex.Message, error: true);
            return false;
        }

        // 无论用户是浏览选择还是手动输入，最终都装进 ...\Class Daily Land 2 里；
        // 已经指向该文件夹时不再重复拼接。
        target = EnsureProductFolder(target);
        InstallPath.Text = target;

        var root = Path.GetPathRoot(target);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(target, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("不能装到磁盘根目录，请换一个文件夹。", error: true);
            return false;
        }

        // 提前踩一次写权限：省得下载完几十 MB 才发现装不进去
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".write-probe.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            SetStatus("这个位置不可写：" + ex.Message, error: true);
            return false;
        }

        return true;
    }

    /// <summary>把用户选的目录规整成 ...\Class Daily Land 2；已经指向该目录时原样返回。</summary>
    private static string EnsureProductFolder(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var leaf = Path.GetFileName(trimmed);

        return string.Equals(leaf, ProductFolderName, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed, ProductFolderName);
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "Error" : "Subtle");
    }

    private void SetBusy(bool busy, bool finished = false)
    {
        InstallButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        InstallPath.IsEnabled = !busy;
        AutoStartCheckBox.IsEnabled = !busy;

        if (busy) CancelButton.Content = "取消";
        else CancelButton.Content = finished ? "关闭" : "取消";
    }

    private void ShowFinishPanel(bool autoStartFailed = false)
    {
        InstallPanel.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Visible;
        FinishPathText.Text = "已安装到：" + _installTarget +
            (autoStartFailed ? Environment.NewLine + "开机自启写入失败，可在主程序「设置 → 通用」里重新开启。" : "");
        RunAppCheckBox.IsChecked = true;
        FinishButton.Focus();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (RunAppCheckBox.IsChecked == true && File.Exists(_appExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _appExe,
                    WorkingDirectory = _installTarget,
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
        }

        Application.Current.Shutdown();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件删不掉不影响安装结果 */ }
    }

    /// <summary>哈希只显示前 12 位：完整 64 位塞进错误提示里没人看。</summary>
    private static string ShortHash(string hash) =>
        string.IsNullOrEmpty(hash) ? "" : hash.Length <= 12 ? hash : hash[..12] + "…";

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗口 = 取消：下载/解压都会在下一个检查点退出
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
