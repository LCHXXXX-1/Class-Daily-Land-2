using ClassDailyLand2.App.Views.Dialogs;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views;

/// <summary>列表里的一行。<see cref="Index"/> 是 homework.json 里的全局下标，删除/修改都按它定位。</summary>
public sealed class HomeworkRowView
{
    public HomeworkRowView(int number, int index, string text, string hint)
    {
        NumberText = $"{number}.";
        Index = index;
        Text = text;
        Hint = hint;
    }

    public string NumberText { get; }
    public int Index { get; }
    public string Text { get; }

    /// <summary>提交时间/排位的那行小字（"10:40 截止" / "补交 · 排最后" / 空）。</summary>
    public string Hint { get; }
}

/// <summary>一组「科目 (日期)」标题 + 组内条目（按原文件顺序切段，不重排）。</summary>
public sealed class HomeworkGroupView
{
    public HomeworkGroupView(string title) => Title = title;

    public string Title { get; }
    public List<HomeworkRowView> Rows { get; } = new();
}

/// <summary>
/// 独立的「编辑作业」窗口（对位截图）：顶部录入、中间按「科目+日期」分组、底部取消/确认。
///
/// ★ 操作是**即时生效**的（Add/Update/RemoveAt 会立刻落盘，主窗与托盘提示同步刷新），
///   所以「取消」靠打开窗口时的快照整体回滚 —— 与设置中心里的 <c>HomeworkDialog</c> 同一套语义。
///
/// ★ 提交时间（1.7.30）：下拉框直接列今天有课的节次，以**那节课的下课**为提交时间；
///   重名的课（下午三节都是数学）用「时间 + 名字」区分，不会选串。
///   另外有「自定义时间」「补交」「不交」三个选项，前两个会先弹一次后果警告。
/// </summary>
public partial class HomeworkEditorWindow : Window
{
    readonly HomeworkManager _hw;
    readonly HomeworkHistoryManager? _history;
    readonly ScheduleManager? _schedule;
    readonly List<HomeworkItem> _backup;

    int _editingIndex = -1;
    bool _committed;

    // 当前下拉框选中的提交时间状态
    string _dueKind = HomeworkDueKinds.None;
    DateTime? _due;
    string _dueLabel = "";

    bool _suppressDueEvent;

    public HomeworkEditorWindow(HomeworkManager homework, HomeworkHistoryManager? history = null,
        ScheduleManager? schedule = null)
    {
        InitializeComponent();

        _hw = homework ?? throw new ArgumentNullException(nameof(homework));
        _history = history;
        _schedule = schedule;
        _backup = _hw.Items.Select(Clone).ToList();

        HistoryButton.IsEnabled = _history is not null;

        ContentInput.TextChanged += (_, _) => UpdateAddButtonState();
        UpdateAddButtonState();

        _courseSignature = CourseSignature();
        BuildDueOptions();
        Reload();
        Loaded += (_, _) => SubjectInput.Focus();

        // 窗口开着的时候可能去改了课表/跨了天：回来时把课程列表重新对一遍（选中项按状态恢复，不会跳）
        Activated += (_, _) =>
        {
            var signature = CourseSignature();
            if (string.Equals(signature, _courseSignature, StringComparison.Ordinal)) return;
            _courseSignature = signature;
            BuildDueOptions();
        };
    }

    string _courseSignature = "";

    string CourseSignature()
    {
        var now = DateTime.Now;
        var slots = _schedule?.Engine?.DaySlots(now);
        if (slots is null) return now.ToString("yyyy-MM-dd");
        return now.ToString("yyyy-MM-dd") + "|" +
               string.Join(";", slots.Select(s => $"{s.Period.Name}@{s.Period.End}={s.Course}"));
    }

    // ————————————————————— 渲染 —————————————————————

    void Reload()
    {
        var groups = new List<HomeworkGroupView>();
        var now = DateTime.Now;

        for (var i = 0; i < _hw.Items.Count; i++)
        {
            var item = _hw.Items[i];
            var title = GroupTitle(item);

            // 连续同名的合成一组（截图里就是这种切段效果），跨组的同名不合并
            if (groups.Count == 0 || !string.Equals(groups[^1].Title, title, StringComparison.Ordinal))
                groups.Add(new HomeworkGroupView(title));

            var content = string.IsNullOrWhiteSpace(item.Content) ? "（空）" : item.Content.Trim();
            groups[^1].Rows.Add(new HomeworkRowView(i + 1, i, content, HomeworkDue.Hint(item, now)));
        }

        GroupsHost.ItemsSource = groups;
        EmptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string GroupTitle(HomeworkItem item)
    {
        var subject = string.IsNullOrWhiteSpace(item.Subject) ? "未分类" : item.Subject.Trim();
        var date = (item.Date ?? "").Trim();
        return date.Length == 0 ? subject : $"{subject} ({date})";
    }

    static HomeworkItem Clone(HomeworkItem item) => new()
    {
        Subject = item.Subject,
        Date = item.Date,
        Content = item.Content,
        Due = item.Due,
        DueKind = item.DueKind,
        DueLabel = item.DueLabel,
        DueFired = item.DueFired,
    };

    // ————————————————————— 提交时间下拉框 —————————————————————

    /// <summary>重建下拉框：今天各节课 + 自定义 / 补交 / 不交，然后按当前状态回选。</summary>
    void BuildDueOptions()
    {
        _suppressDueEvent = true;
        try
        {
            DueBox.Items.Clear();
            foreach (var option in HomeworkDueOptions.Build(_schedule?.Engine, DateTime.Now, _dueKind, _due, _dueLabel))
                DueBox.Items.Add(option);

            DueBox.SelectedItem = HomeworkDueOptions.Find(DueBox.Items.OfType<HomeworkDueOption>(), _dueKind, _due);
        }
        finally
        {
            _suppressDueEvent = false;
        }

        UpdateDueHint();
    }

    /// <summary>下拉框旁边那句"这条作业会排在哪"。</summary>
    void UpdateDueHint()
    {
        if (DueHint is not null) DueHint.Text = HomeworkDueOptions.HintFor(_dueKind, _due, DateTime.Now);
    }

    void Due_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDueEvent || DueBox.SelectedItem is not HomeworkDueOption option) return;

        // 「自定义时间…」：先警告后果，再让用户填时间；取消就退回原来的选择
        if (option.IsPicker)
        {
            if (!WarnCustomTime()) { BuildDueOptions(); return; }

            if (!HomeworkDueDialogs.TryPickTime(this, _due, out var picked))
            {
                BuildDueOptions();
                return;
            }

            _dueKind = HomeworkDueKinds.Custom;
            _due = picked;
            _dueLabel = $"自定义 · {HomeworkDue.Describe(picked, DateTime.Now)}";
            BuildDueOptions();
            return;
        }

        if (option.Kind == HomeworkDueKinds.Late && !WarnLate())
        {
            BuildDueOptions();
            return;
        }

        _dueKind = option.Kind;
        _due = option.Due;
        _dueLabel = option.Text;
        UpdateDueHint();
    }

    bool WarnCustomTime() => HomeworkDueOptions.WarnCustomTime(this);

    bool WarnLate() => HomeworkDueOptions.WarnLate(this);

    void ResetDue()
    {
        _dueKind = HomeworkDueKinds.None;
        _due = null;
        _dueLabel = "";
        BuildDueOptions();
    }

    void LoadDue(HomeworkItem item)
    {
        _dueKind = (item.DueKind ?? "").Trim();
        _due = item.DueTime;
        _dueLabel = item.DueLabel ?? "";
        if (_dueKind.Length == 0) _dueKind = _due is null ? HomeworkDueKinds.None : HomeworkDueKinds.Custom;
        BuildDueOptions();
    }

    // ————————————————————— 添加 / 修改 —————————————————————

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var subject = SubjectInput.Text.Trim();
        var content = ContentInput.Text.TrimEnd();

        if (string.IsNullOrWhiteSpace(content)) return;

        var previousDate = _editingIndex >= 0 && _editingIndex < _hw.Items.Count
            ? (_hw.Items[_editingIndex].Date ?? "")
            : "";
        var previousFired = _editingIndex >= 0 && _editingIndex < _hw.Items.Count
            ? (_hw.Items[_editingIndex].DueFired ?? "")
            : "";
        var previousDue = _editingIndex >= 0 && _editingIndex < _hw.Items.Count
            ? (_hw.Items[_editingIndex].Due ?? "")
            : "";

        var item = BuildItem(subject, content, previousDate, previousDue, previousFired);

        if (_editingIndex >= 0)
        {
            // 「修改」模式：写回原来那一行
            _hw.Update(_editingIndex, item);
            CancelEdit(clearInputs: true);
        }
        else
        {
            _hw.Add(item);
            ContentInput.Text = "";
            ContentInput.Focus();
        }

        Reload();
    }

    /// <summary>把当前输入拼成一条作业；提交时间变了就把「已触发」标记清掉。</summary>
    HomeworkItem BuildItem(string subject, string content, string previousDate, string previousDue, string previousFired)
    {
        var dueText = _due is null ? "" : HomeworkDue.ToText(_due.Value);
        var fired = string.Equals(dueText, previousDue ?? "", StringComparison.Ordinal)
            ? (previousFired ?? "")
            : "";

        // 补交：没有具体提交时间，只标记"排最后"
        if (_dueKind == HomeworkDueKinds.Late)
        {
            dueText = "";
            fired = "";
        }

        return new HomeworkItem
        {
            Subject = subject,
            Content = content,
            // 日期备注：有提交时间就跟着走，没有就保留原来手填的
            Date = _due is null ? (previousDate ?? "").Trim() : _due.Value.ToString("yyyy-MM-dd"),
            Due = dueText,
            DueKind = _dueKind,
            DueLabel = _dueLabel,
            DueFired = fired,
        };
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<HomeworkRowView>(sender) is not { } row) return;
        if (row.Index < 0 || row.Index >= _hw.Items.Count) return;

        var item = _hw.Items[row.Index];

        _editingIndex = row.Index;
        SubjectInput.Text = item.Subject;
        ContentInput.Text = item.Content;
        LoadDue(item);

        AddButton.Content = "保存修改";
        CancelEditButton.Visibility = Visibility.Visible;
        UpdateAddButtonState();

        ContentInput.Focus();
        ContentInput.CaretIndex = ContentInput.Text.Length;
    }

    void CancelEdit_Click(object sender, RoutedEventArgs e) => CancelEdit(clearInputs: true);

    void CancelEdit(bool clearInputs)
    {
        _editingIndex = -1;
        AddButton.Content = "添加作业";
        CancelEditButton.Visibility = Visibility.Collapsed;

        if (clearInputs) ContentInput.Text = "";

        ResetDue();
        UpdateAddButtonState();
    }

    // ————————————————————— 删除 / 整组清除 —————————————————————

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<HomeworkRowView>(sender) is not { } row) return;

        _hw.RemoveAt(row.Index);

        // 删完下标会前移：正在改的那行跟着调整
        if (_editingIndex == row.Index) CancelEdit(clearInputs: true);
        else if (_editingIndex > row.Index) _editingIndex--;

        Reload();
    }

    void ClearGroup_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<HomeworkGroupView>(sender) is not { } group) return;

        var doomed = new HashSet<int>(group.Rows.Select(r => r.Index));
        _hw.ReplaceAll(_hw.Items.Where((_, index) => !doomed.Contains(index)).Select(Clone).ToList());

        CancelEdit(clearInputs: true);
        Reload();
    }

    // ————————————————————— 历史记录 —————————————————————

    void History_Click(object sender, RoutedEventArgs e)
    {
        if (_history is null) return;

        var window = new HomeworkHistoryWindow(_history, _hw, OnHistoryRestored) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>
    /// 从历史恢复后：把「取消」的基线换成恢复后的内容。
    /// 否则用户恢复完直接关窗，OnClosing 会拿打开时的旧快照把恢复结果覆盖回去。
    /// </summary>
    void OnHistoryRestored()
    {
        _backup.Clear();
        _backup.AddRange(_hw.Items.Select(Clone));
        CancelEdit(clearInputs: true);
        Reload();
    }

    // ————————————————————— 确认 / 取消 —————————————————————

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _committed = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // 没点「确认」（点取消、按 X、Alt+F4 都算）：用打开时的快照整体回滚
        if (!_committed) _hw.ReplaceAll(_backup);

        base.OnClosing(e);
    }

    // ————————————————————— 小工具 —————————————————————

    static T? TagOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.Tag as T;

    void UpdateAddButtonState() =>
        AddButton.IsEnabled = !string.IsNullOrWhiteSpace(ContentInput.Text);
}
