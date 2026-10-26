using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Updates;
using System.Windows;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 更新相关的确认提示。全部画在设置窗口内部（用户明确要求过"提示不弹系统框"）。
/// </summary>
public static class UpdateDialogs
{
    /// <summary>
    /// 「发现新版本」确认框。
    ///
    /// <paramref name="onInstall"/> 用户点「立即更新」；
    /// <paramref name="onLater"/> 用户点「稍后再说」或按 ESC —— 这两种都要留痕，
    /// 否则下次启动检查又会弹出来烦用户。
    /// </summary>
    public static void ShowNewVersion(DependencyObject child, UpdateOffer offer,
                                      Action onInstall, Action onLater)
    {
        var size = offer.Size > 0 ? $"，约 {FormatSize(offer.Size)}" : "";
        var lines = new List<string>
        {
            $"版本 {offer.Version}（{offer.ReleaseDate}）{size}",
            "",
        };

        if (offer.Changelog.Count > 0)
        {
            foreach (var item in offer.Changelog.Take(8)) lines.Add("· " + item);
            if (offer.Changelog.Count > 8) lines.Add($"· ……还有 {offer.Changelog.Count - 8} 条");
            lines.Add("");
        }

        lines.Add("更新期间程序会短暂关闭，设置与插件不受影响。");

        var title = offer.IsMandatory ? $"发现新版本 {offer.Version}（强制）" : $"发现新版本 {offer.Version}";

        DialogLauncher.Confirm(
            child,
            title,
            string.Join(Environment.NewLine, lines),
            onOk: onInstall,
            okText: "立即更新",
            cancelText: "稍后再说",
            onCancel: onLater);
    }

    /// <summary>「资源包已更新」提示（不打断操作，用 InfoBar 而不是确认框）。</summary>
    public static void ShowResourcesUpdated(DependencyObject child, string version) =>
        DialogLauncher.Toast(child, InfoBarSeverity.Success, "资源包已更新",
            $"图标 / 节假日数据已更新到 {version}，部分内容下次启动生效。");

    static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }
}
