using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ClassDailyLand.Installer.Offline.Services;
using ClassDailyLand.UpdateContract;
using Microsoft.Win32;

namespace ClassDailyLand.Installer.Offline;

public partial class MainWindow : Window
{
    private const string ProductFolderName = "Class Daily Land 2";

    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _installTarget = "";
    private string _appExe = "";

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

        // Progress<T> 会回到创建它的线程（UI 线程）上回调，所以这里直接动控件是安全的
        var progress = new Progress<DownloadProgress>(report =>
        {
            Progress.Value = Math.Clamp(report.Percent, 0, 100);
            if (!string.IsNullOrWhiteSpace(report.Message)) SetStatus(report.Message!, error: false);
        });

        var installerDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        try
        {
            Progress.Value = 2;

            // ① 运行环境：离线安装器优先找同目录里的运行库安装包，不联网下载。
            SetStatus("检查 .NET 10 桌面运行时…", error: false);

            var runtimeInstalled = false;
            if (!RuntimeChecker.HasDesktopRuntime(out var runtimeDetail))
            {
                var localRuntime = FindLocalRuntime(installerDir);
                if (localRuntime is null)
                    throw new InstallFailedException("缺少 .NET 10 桌面运行时。请把 windowsdesktop-runtime-*-win-x64.exe 放到安装程序同目录后重试。");

                SetStatus("正在安装 .NET 10 桌面运行时（会弹出系统授权提示）…", error: false);
                await RuntimeChecker.InstallAsync(localRuntime, token);

                if (!RuntimeChecker.HasDesktopRuntime(out runtimeDetail))
                    throw new InstallFailedException("运行库安装完成后仍然检测不到：" + runtimeDetail);

                runtimeInstalled = true;
                SetStatus("运行库已就绪。", error: false);
            }

            Directory.CreateDirectory(target);

            // ② 离线主程序包：从安装程序同目录读取，不访问 version.json。
            Progress.Value = runtimeInstalled ? 18 : 22;
            SetStatus("正在查找离线安装包…", error: false);

            var zipPath = FindLocalPackage(installerDir);
            if (zipPath is null)
                throw new InstallFailedException($"没找到离线安装包。请把 app.zip 放到安装程序同目录：{installerDir}");

            // ③ 可选校验：同目录放 app.zip.sha256 才会验；没有就直接装。
            var expectedHash = TryReadSha256(zipPath);
            if (!string.IsNullOrEmpty(expectedHash))
            {
                SetStatus("正在校验离线安装包…", error: false);
                Progress.Value = 35;

                var actual = await Task.Run(() => Hashing.ComputeFileSha256(zipPath), token);
                if (!Hashing.Sha256Equals(expectedHash!, actual))
                    throw new InstallFailedException(
                        $"离线安装包校验失败（期望 {ShortHash(expectedHash)}，实际 {ShortHash(actual)}）");
            }

            // ④ 解压
            SetStatus("解压到 " + target + " …", error: false);
            await ZipInstaller.ExtractAsync(zipPath, target, progress,
                runtimeInstalled ? 45 : 40, 88, token);

            // ⑤ 快捷方式 + 卸载信息
            Progress.Value = 92;
            SetStatus("创建快捷方式与卸载信息…", error: false);
            var appExe = Path.Combine(target, SetupEndpoints.AppExeName);
            ShortcutService.CreateAll(target, appExe);
            UninstallRegistration.Register(target);

            Progress.Value = 100;
            SetStatus("安装完成。", error: false);
            SetBusy(false, finished: true);

            // ⑥ 不再直接启动：切到完成页，等待用户确认
            _installTarget = target;
            _appExe = appExe;
            ShowFinishPanel();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetStatus("已取消，未完成的文件保留在安装目录里，可以直接重试。", error: false);
            SetBusy(false);
        }
        catch (Exception ex)
        {
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

        if (busy) CancelButton.Content = "取消";
        else CancelButton.Content = finished ? "关闭" : "取消";
    }

    private void ShowFinishPanel()
    {
        InstallPanel.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Visible;
        FinishPathText.Text = "已安装到：" + _installTarget;
        RunAppCheckBox.IsChecked = true;
        FinishButton.Focus();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (RunAppCheckBox.IsChecked == true)
        {
            if (!File.Exists(_appExe))
            {
                MessageBox.Show(this,
                    "没找到 " + SetupEndpoints.AppExeName + "，请检查安装目录。",
                    "Class Daily Land 2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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
                MessageBox.Show(this,
                    "主程序启动失败，你可以稍后从桌面快捷方式打开。",
                    "Class Daily Land 2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        Close();
    }

    private static string? FindLocalPackage(string dir)
    {
        string[] preferred =
        {
            "app.zip",
            "ClassDailyLand-app.zip",
            "ClassDailyLand2-app.zip",
            "Class Daily Land 2.zip",
            "Class Daily Land 2-stable.zip",
        };

        foreach (var name in preferred)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("Class", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CDL", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch
        {
            // 列目录失败时继续走显式文件名查找；真正没找到会在安装逻辑里报人话错误。
        }

        return null;
    }

    private static string? FindLocalRuntime(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "windowsdesktop-runtime-*-win-x64.exe", SearchOption.TopDirectoryOnly))
                return file;
        }
        catch
        {
            // 找不到运行库安装包时由安装逻辑提示用户。
        }

        return null;
    }

    private static string? TryReadSha256(string packagePath)
    {
        var shaPath = packagePath + ".sha256";
        if (!File.Exists(shaPath)) return null;

        try
        {
            var text = File.ReadAllText(shaPath).Trim();
            if (text.Length == 0) return null;

            var first = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return first.Length == 0 ? null : first[0];
        }
        catch
        {
            return null;
        }
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
