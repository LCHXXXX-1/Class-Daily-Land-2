using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views;

/// <summary>列表里的一行。<see cref="Index"/> 是 homework.json 里的全局下标，删除/修改都按它定位。</summary>
public sealed class HomeworkRowView
{
    public HomeworkRowView(int number, int index, string text)
    {
        NumberText = $"{number}.";
        Index = index;
        Text = text;
    }

    public string NumberText { get; }
    public int Index { get; }
    public string Text { get; }
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
/// </summary>
public partial class HomeworkEditorWindow : Window
{
    readonly HomeworkManager _hw;
    readonly List<HomeworkItem> _backup;

    int _editingIndex = -1;
    bool _committed;

    public HomeworkEditorWindow(HomeworkManager homework)
    {
        InitializeComponent();

        _hw = homework ?? throw new ArgumentNullException(nameof(homework));
        _backup = _hw.Items.Select(Clone).ToList();

        ContentInput.TextChanged += (_, _) => UpdateAddButtonState();
        UpdateAddButtonState();

        Reload();
        Loaded += (_, _) => SubjectInput.Focus();
    }

    // ————————————————————— 渲染 —————————————————————

    void Reload()
    {
        var groups = new List<HomeworkGroupView>();

        for (var i = 0; i < _hw.Items.Count; i++)
        {
            var item = _hw.Items[i];
            var title = GroupTitle(item);

            // 连续同名的合成一组（截图里就是这种切段效果），跨组的同名不合并
            if (groups.Count == 0 || !string.Equals(groups[^1].Title, title, StringComparison.Ordinal))
                groups.Add(new HomeworkGroupView(title));

            var content = string.IsNullOrWhiteSpace(item.Content) ? "（空）" : item.Content.Trim();
            groups[^1].Rows.Add(new HomeworkRowView(i + 1, i, content));
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
    };

    // ————————————————————— 添加 / 修改 —————————————————————

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var subject = SubjectInput.Text.Trim();
        var date = DateInput.Text.Trim();
        var content = ContentInput.Text.TrimEnd();

        if (string.IsNullOrWhiteSpace(content)) return;

        if (_editingIndex >= 0)
        {
            // 「修改」模式：写回原来那一行
            _hw.Update(_editingIndex, new HomeworkItem { Subject = subject, Date = date, Content = content });
            CancelEdit(clearInputs: true);
        }
        else
        {
            _hw.Add(new HomeworkItem { Subject = subject, Date = date, Content = content });
            ContentInput.Text = "";
            ContentInput.Focus();
        }

        Reload();
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<HomeworkRowView>(sender) is not { } row) return;
        if (row.Index < 0 || row.Index >= _hw.Items.Count) return;

        var item = _hw.Items[row.Index];

        _editingIndex = row.Index;
        SubjectInput.Text = item.Subject;
        DateInput.Text = item.Date;
        ContentInput.Text = item.Content;

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
