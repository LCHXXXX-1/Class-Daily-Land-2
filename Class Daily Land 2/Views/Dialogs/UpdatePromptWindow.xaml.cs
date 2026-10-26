using System.ComponentModel;
using System.Windows;
using ClassDailyLand2.App.Updates;

namespace ClassDailyLand2.App.Views.Dialogs;

public partial class UpdatePromptWindow : Window
{
    readonly UpdateService _updates;
    readonly AppController? _ctx;
    readonly UpdateOffer _offer;

    bool _allowClose;
    bool _installing;
    bool _started;

    public UpdatePromptWindow(UpdateService updates, UpdateOffer offer, AppController? ctx = null)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _ctx = ctx;
        _offer = offer ?? throw new ArgumentNullException(nameof(offer));

        InitializeComponent();

        TitleText.Text = _offer.IsMandatory
            ? $"发现新版本 {_offer.Version}（强制更新）"
            : $"发现新版本 {_offer.Version}";
        VersionText.Text = $"当前版本：{AppVersion.Current}    新版本：{_offer.Version}";

        if (!string.IsNullOrWhiteSpace(_offer.ReleaseDate))
        {
            DateText.Text = $"发布日期：{_offer.ReleaseDate}";
            DateText.Visibility = Visibility.Visible;
        }

        ChangelogItems.ItemsSource = BuildChangelog(_offer);

        if (_offer.IsMandatory)
        {
            LaterButton.Visibility = Visibility.Collapsed;
            MandatoryHintText.Visibility = Visibility.Visible;
            Title = "强制更新";
        }

        Loaded += (_, _) =>
        {
            Activate();
            InstallButton.Focus();
        };
    }

    static IReadOnlyList<string> BuildChangelog(UpdateOffer offer)
    {
        var lines = new List<string>();

        if (offer.Changelog.Count > 0)
        {
            foreach (var item in offer.Changelog)
                lines.Add("· " + item);
        }
        else
        {
            lines.Add("服务器未提供更新说明。");
        }

        lines.Add("");
        lines.Add("更新会替换程序文件，settings 与 plugins 会保留。");

        if (offer.Size > 0)
            lines.Add($"包大小：{FormatSize(offer.Size)}");

        return lines;
    }

    static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_offer.IsMandatory && !_allowClose)
        {
            e.Cancel = true;
            ShowStatus("强制更新不能跳过，请点击「立即更新」。");
            return;
        }

        if (!_allowClose && !_started && !_installing)
        {
            try
            {
                _updates.SkipVersion(_offer);
            }
            catch
            {
                // 关闭路径不能因为记录跳过版本失败而卡住。
            }
        }

        base.OnClosing(e);
    }

    void Later_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;

        try
        {
            _updates.SkipVersion(_offer);
        }
        catch
        {
            // 跳过记录失败不影响窗口关闭。
        }

        Close();
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;

        _installing = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ShowStatus("正在启动更新器…");

        try
        {
            var result = await _updates.ApplyAsync(_offer);

            if (!result.Ok)
            {
                ShowStatus("启动更新失败：" + result.Message);
                _installing = false;
                InstallButton.IsEnabled = true;
                if (!_offer.IsMandatory)
                    LaterButton.IsEnabled = true;
                return;
            }

            _started = true;
            _allowClose = true;
            ShowStatus("更新器已启动，主程序将隐藏，等待更新器完成替换…");

            // 这里绝不能 Shutdown / Exit：只隐藏主程序，让更新器继续下载与替换；
            // 真正关闭主进程由更新器在替换文件前执行。
            _ctx?.HideForUpdater();
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus("启动更新失败：" + ex.Message);
            _installing = false;
            InstallButton.IsEnabled = true;
            if (!_offer.IsMandatory)
                LaterButton.IsEnabled = true;
        }
    }

    void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
