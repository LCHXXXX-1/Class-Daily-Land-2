using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views;

/// <summary>历史日期列表的一行。</summary>
public sealed class HomeworkHistoryDateRow
{
    public HomeworkHistoryDateRow(HomeworkSnapshotInfo info) => Info = info;

    public HomeworkSnapshotInfo Info { get; }

    public string DateText => Info.DateText;
    public string Summary => $"{Info.Count} 条作业 · {Info.WeekdayText}";
}

/// <summary>
/// 「查看历史记录」窗口：左侧列出有快照的日期，右侧只读显示当天的作业。
/// 恢复走窗口内的二次确认（不弹系统 MessageBox），确认后调用
/// <see cref="HomeworkManager.ReplaceAll"/> 覆盖当前作业。
/// </summary>
public partial class HomeworkHistoryWindow : Window
{
    readonly HomeworkHistoryManager _history;
    readonly HomeworkManager _homework;
    readonly Action? _onRestored;

    HomeworkSnapshotInfo? _selected;
    HomeworkSnapshotInfo? _pending;

    public HomeworkHistoryWindow(HomeworkHistoryManager history, HomeworkManager homework, Action? onRestored = null)
    {
        InitializeComponent();

        _history = history ?? throw new ArgumentNullException(nameof(history));
        _homework = homework ?? throw new ArgumentNullException(nameof(homework));
        _onRestored = onRestored;

        LoadSnapshots();
    }

    // ————————————————————— 列表 —————————————————————

    void LoadSnapshots()
    {
        var rows = _history.ListSnapshots().Select(s => new HomeworkHistoryDateRow(s)).ToList();

        DatesList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0)
        {
            SnapshotTitle.Text = "还没有可查看的历史记录";
            RestoreButton.IsEnabled = false;
            return;
        }

        DatesList.SelectedIndex = 0;   // 最新的一天
    }

    void OnDateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DatesList.SelectedItem is not HomeworkHistoryDateRow row) return;

        _selected = row.Info;
        var items = _history.ReadSnapshot(row.Info.Path);
        var lines = items.Select(FormatLine).ToList();

        ItemsHost.ItemsSource = lines;
        PreviewEmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotTitle.Text = $"{row.Info.Date:yyyy 年 M 月 d 日}（{row.Info.WeekdayText}）的作业 · 只读";
        RestoreButton.IsEnabled = true;

        HideConfirm();
    }

    static string FormatLine(HomeworkItem item)
    {
        var text = item.Display;
        if (string.IsNullOrWhiteSpace(text)) text = "（空）";

        var date = (item.Date ?? "").Trim();
        return date.Length == 0 ? text : $"{text}（{date}）";
    }

    // ————————————————————— 恢复 —————————————————————

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        _pending = _selected;
        ConfirmText.Text = $"用 {_pending.DateText} 的作业覆盖当前作业？";
        FooterNormal.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    void CancelRestore_Click(object sender, RoutedEventArgs e) => HideConfirm();

    void HideConfirm()
    {
        _pending = null;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        FooterNormal.Visibility = Visibility.Visible;
    }

    void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        var info = _pending;
        if (info is null) return;

        var items = _history.ReadSnapshot(info.Path)
            .Select(item => new HomeworkItem
            {
                Subject = item.Subject,
                Date = item.Date,
                Content = item.Content,
                // 提交时间也一起恢复：否则一恢复就全变成"没设提交时间"
                Due = item.Due,
                DueKind = item.DueKind,
                DueLabel = item.DueLabel,
                DueFired = item.DueFired,
            })
            .ToList();

        // ReplaceAll 会落盘 → 触发 HomeworkManager.Changed → 自动存下今天这一份快照
        _homework.ReplaceAll(items);
        _onRestored?.Invoke();

        HideConfirm();
        Close();
    }

    // ————————————————————— 其他 —————————————————————

    void OpenFolder_Click(object sender, RoutedEventArgs e) => DialogLauncher.OpenFolder(_history.FolderPath);

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
