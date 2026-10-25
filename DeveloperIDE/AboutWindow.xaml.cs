using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace DeveloperIDE;

/// <summary>
/// 关于：应用名、版本号、检查更新。
///
/// 检查与拉起都在 <see cref="UpdateLauncher"/>：启动时的静默检查和这里的主动检查走同一条路，
/// 免得两边参数拼法走岔（拼错 <c>--target</c> 是会覆盖错目录的）。
/// </summary>
public partial class AboutWindow : Window
{
    readonly AppConfig _config = AppConfig.Load();
    bool _checking;

    public AboutWindow()
    {
        InitializeComponent();

        AppNameText.Text = string.IsNullOrWhiteSpace(_config.AppName) ? "DeveloperIDE" : _config.AppName;
        VersionText.Text = "v" + UpdateLauncher.LocalVersion;
    }

    // ─────────────────────────── 检查更新 ───────────────────────────

    async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_checking) return;

        _checking = true;
        CheckButton.IsEnabled = false;
        CheckButton.Content = "正在检查…";

        try
        {
            // 固定地址先试，拿不到再退到 config.json 里的兜底源
            var remote = await UpdateLauncher.CheckManualAsync(_config.VersionUrlFallback);
            if (remote is null)
            {
                Say("检查失败：更新源没返回合法的 version.json。");
                return;
            }

            if (!UpdateLauncher.IsNewer(remote, UpdateLauncher.LocalVersion))
            {
                Say($"当前已是最新版本 v{UpdateLauncher.LocalVersion}（远端 {remote.Version}，来自 {remote.Source}）。");
                return;
            }

            var label = string.IsNullOrWhiteSpace(remote.Date) ? remote.Version : $"{remote.Version}（{remote.Date}）";
            var ask = MessageBox.Show(this,
                $"发现新版本 {label}\n\n是否立即更新？IDE 会自动关闭，更新完成后重新启动。",
                "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;

            if (UpdateLauncher.TryStart(remote, out var error))
            {
                // 立刻松手：更新器自己也会等 1.5 秒才动手
                Application.Current.Shutdown();
                return;
            }

            Say(error);
        }
        catch (HttpRequestException)
        {
            // 只有用户主动点了「检查更新」才允许弹这个框
            Say("网络连接失败，请检查网络或稍后再试");
        }
        catch (TaskCanceledException)
        {
            Say("网络连接失败，请检查网络或稍后再试（10 秒内没响应）");
        }
        catch (Exception ex)
        {
            // 解析不出来之类的问题，给出原始信息便于排查
            Say("检查更新失败：" + ex.Message);
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
            CheckButton.Content = "检查更新";
        }
    }

    void Say(string message) =>
        MessageBox.Show(this, message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
}
