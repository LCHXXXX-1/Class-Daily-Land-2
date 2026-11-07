using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 作业编辑器（设置中心「作业」页）：**一个科目一张卡片**，卡片颜色由科目名固定下来
/// （同一科目永远同一个颜色，换台机器/重排都不变），卡片可以直接拖动换顺序。
///
/// 每一条作业自带「提交时间」下拉框 —— 规则与独立「编辑作业」窗口完全一致
/// （<see cref="HomeworkDueOptions"/> 一份实现两处用）：
///   今天各节的下课 / 自定义时间 / 补交 / 不交；补交与自定义先弹后果警告。
///
/// 改动即时生效：输入框失焦、选完提交时间、拖完卡片都会立刻落盘并广播 Changed。
/// </summary>
public sealed class HomeworkEditor : UserControl
{
    /// <summary>一条作业在界面上的零件。</summary>
    sealed class RowState
    {
        public required HomeworkItem Item { get; init; }
        public required TextBox Content { get; init; }
        public required ComboBox Due { get; init; }
        public bool Suppress;
    }

    /// <summary>一个科目的卡片。</summary>
    sealed class CardState
    {
        public required TextBox Subject { get; init; }
        public required Border Shell { get; init; }
        public required Border Accent { get; init; }
        public required StackPanel HeaderHost { get; init; }
        public required StackPanel RowsHost { get; init; }
        public required TextBlock Count { get; init; }
        public required Border Dot { get; init; }
        public List<RowState> Rows { get; } = new();
    }

    /// <summary>科目配色（同一个科目永远同一个颜色）。深浅主题都能看清的中等饱和度。</summary>
    static readonly string[] SubjectPalette =
    {
        "#0A6ED1", "#107C10", "#C239B3", "#D13438", "#CA5010",
        "#038387", "#8764B8", "#4F6BED", "#B4009E", "#498205",
    };

    readonly HomeworkManager _hw;
    readonly ScheduleManager? _schedule;
    readonly Func<string> _folderPath;
    readonly StackPanel _cardsHost = new() { Orientation = Orientation.Vertical };
    readonly List<CardState> _cards = new();
    readonly Dictionary<string, Color> _subjectColors = new(StringComparer.Ordinal);
    readonly TextBlock _emptyHint;

    readonly ScrollViewer _scroll = new();
    string _savedSignature = "";

    CardState? _dragCard;
    TouchDevice? _touchDevice;
    double _dragStartY;
    bool _dragging;
    bool _endingDrag;

    /// <summary>面包屑的父页（它不是 DialogPanel，单独给一个属性）。</summary>
    public string ParentPageTitle => "作业";

    public HomeworkEditor(SettingsViewModel vm, bool embedded)
        : this((vm ?? throw new ArgumentNullException(nameof(vm))).Ctx.Homework,
               vm.Ctx.Schedule,
               () => vm.ConfigPath,
               embedded)
    {
    }

    /// <summary>直接给数据源（离屏自检 / 测试用，不依赖设置中心的 ViewModel）。</summary>
    public HomeworkEditor(HomeworkManager homework, ScheduleManager schedule,
        Func<string>? folderPath = null, bool embedded = true)
    {
        _hw = homework ?? throw new ArgumentNullException(nameof(homework));
        _schedule = schedule;
        _folderPath = folderPath ?? (() => "");

        var root = new DockPanel();

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(Tool("保存", SaveNow, accent: true));
        toolbar.Children.Add(Tool("＋ 新增作业", AddCard));
        toolbar.Children.Add(Tool("导出 CSV", ExportCsv));
        toolbar.Children.Add(Tool("导出 JSON", ExportJson));
        toolbar.Children.Add(Tool("导入 JSON", ImportJson));
        toolbar.Children.Add(Tool("打开作业文件夹", () => DialogLauncher.OpenFolder(_folderPath())));

        var autoHint = new TextBlock
        {
            Text = "改动会自动保存",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        autoHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        toolbar.Children.Add(autoHint);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var tip = new TextBlock
        {
            Text = "拖动卡片标题可以给科目换顺序；每条作业的「提交时间」决定它在作业区是顶到第一行还是沉到最底下。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 10),
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(tip, Dock.Top);
        root.Children.Add(tip);

        _emptyHint = new TextBlock
        {
            Text = "还没有作业。点上面的「＋ 新增作业」开始。",
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        _scroll.Content = _cardsHost;
        _scroll.MaxHeight = embedded ? 560 : double.PositiveInfinity;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Theme.TouchMode.EnableTouchScroll(_scroll);

        var host = new Grid();
        host.Children.Add(_scroll);
        host.Children.Add(_emptyHint);
        root.Children.Add(host);

        Content = root;

        _cardsHost.MouseMove += OnCardsMouseMove;
        _cardsHost.MouseLeftButtonUp += (_, _) => EndDrag();
        _cardsHost.LostMouseCapture += (_, _) => EndDrag();

        // 触屏：手指按住卡片标题拖动。这些事件必须自己接管 ——
        // 不接管的话 ScrollViewer 的 PanningMode 会把触摸当滚动手势吃掉，卡片根本拖不动。
        _cardsHost.PreviewTouchMove += OnCardsTouchMove;
        _cardsHost.PreviewTouchUp += OnCardsTouchUp;
        _cardsHost.LostTouchCapture += (_, _) => EndDrag();

        // 关设置窗 / 切页面时焦点元素不一定收到 LostFocus，这里补一枪，免得刚打的字丢了
        Unloaded += (_, _) =>
        {
            UnhookSchedule();
            Commit(rebuild: false);
        };

        Loaded += (_, _) =>
        {
            HookSchedule();
            RefreshDueCombos();   // 页面切回来时，课表可能已经改过了
        };

        Rebuild();
    }

    // ————————————————————— 课表变化时刷新提交时间 —————————————————————

    bool _scheduleHooked;

    void HookSchedule()
    {
        if (_schedule is null || _scheduleHooked) return;
        _scheduleHooked = true;
        _schedule.Changed += OnScheduleChanged;
    }

    void UnhookSchedule()
    {
        if (_schedule is null || !_scheduleHooked) return;
        _scheduleHooked = false;
        _schedule.Changed -= OnScheduleChanged;
    }

    /// <summary>课表保存后：重新算每条"按节次"的作业时间，并重建下拉框。</summary>
    void OnScheduleChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshDueCombos);
            return;
        }
        RefreshDueCombos();
    }

    void RefreshDueCombos()
    {
        foreach (var card in _cards)
            foreach (var row in card.Rows)
                RefreshDueCombo(row);
    }

    // ————————————————————— 渲染 —————————————————————

    /// <summary>按 <see cref="_hw"/> 重建全部卡片（结构增删 / 改名 / 排序之后调）。</summary>
    public void Rebuild()
    {
        var offset = _scroll.VerticalOffset;

        _cards.Clear();
        _cardsHost.Children.Clear();

        // 按科目归组，保持首次出现的顺序
        var order = new List<string>();
        var buckets = new Dictionary<string, List<HomeworkItem>>(StringComparer.Ordinal);
        foreach (var item in _hw.Items)
        {
            var subject = (item.Subject ?? "").Trim();
            if (!buckets.TryGetValue(subject, out var list))
            {
                buckets[subject] = list = new List<HomeworkItem>();
                order.Add(subject);
            }
            list.Add(item);
        }

        // 已经删掉的科目：把它的配色记录也清掉，别继续占着"用量"
        var alive = new HashSet<string>(order, StringComparer.Ordinal);
        foreach (var gone in _subjectColors.Keys.Where(k => !alive.Contains(k)).ToList())
            _subjectColors.Remove(gone);

        foreach (var subject in order)
            _cardsHost.Children.Add(BuildCard(subject, buckets[subject]));

        var empty = _hw.Items.Count == 0;
        _emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _savedSignature = Signature(_hw.Items);

        if (offset > 0)
            Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
    }

    FrameworkElement BuildCard(string subject, List<HomeworkItem> items)
    {
        var color = SubjectColor(subject);
        var accent = Frozen(color);

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var subjectBox = new TextBox
        {
            Text = subject,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 70,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "学科名（回车或点别处生效；改名会自动换颜色）",
        };
        subjectBox.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
        subjectBox.SetResourceReference(TextBox.CaretBrushProperty, "TextFillColorPrimaryBrush");
        // 学科名变了才重建（换颜色 / 可能和别的卡片合并）；没变就只落盘 ——
        // 否则用户点一下这一行的时间下拉框，LostFocus 把整棵树重建了，那一下点击就丢了
        var known = subject;
        subjectBox.LostFocus += (_, _) => CommitSubject(subjectBox, known);
        subjectBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitSubject(subjectBox, known);
        };

        var count = new TextBlock
        {
            Text = $"{items.Count} 条",
            FontSize = 11.5,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 圆点
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 学科名
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 条数
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // ＋
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 移除
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 拖动提示

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(subjectBox, 1);
        Grid.SetColumn(count, 2);
        headerGrid.Children.Add(dot);
        headerGrid.Children.Add(subjectBox);
        headerGrid.Children.Add(count);

        var headerHost = new StackPanel();

        var addButton = MiniButton("＋", "给这个科目再加一条作业");
        var removeButton = MiniButton("移除", "移除这个科目下的所有作业");

        Grid.SetColumn(addButton, 3);
        Grid.SetColumn(removeButton, 4);
        addButton.Margin = new Thickness(0, 0, 6, 0);
        removeButton.Margin = new Thickness(0, 0, 6, 0);
        headerGrid.Children.Add(addButton);
        headerGrid.Children.Add(removeButton);

        var grip = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeNS,
            ToolTip = "按住拖动，给科目换顺序",
        };
        grip.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Grid.SetColumn(grip, 5);
        headerGrid.Children.Add(grip);

        headerHost.Children.Add(headerGrid);

        var rowsHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var headerBorder = new Border
        {
            Background = Frozen(Color.FromArgb(0x16, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 8, 7),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.SizeNS,
            Child = headerHost,
            Tag = "card-header",
        };
        headerBorder.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        headerBorder.PreviewTouchDown += (_, e) => BeginDragTouch(e);

        var body = new StackPanel { Margin = new Thickness(15, 10, 13, 11) };
        body.Children.Add(headerBorder);
        body.Children.Add(rowsHost);

        var accentBar = new Border
        {
            Width = 4,
            Background = accent,
            CornerRadius = new CornerRadius(10, 0, 0, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(accentBar);
        grid.Children.Add(body);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Frozen(Color.FromArgb(0x4D, color.R, color.G, color.B)),
            Margin = new Thickness(0, 0, 2, 10),
            Child = grid,
            Tag = "card",
        };
        shell.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

        var card = new CardState
        {
            Subject = subjectBox,
            Shell = shell,
            Accent = accentBar,
            HeaderHost = headerHost,
            RowsHost = rowsHost,
            Count = count,
            Dot = dot,
        };

        _cards.Add(card);

        foreach (var item in items)
            BuildRow(card, item);

        addButton.Click += (_, _) => AddRow(card);
        removeButton.Click += (_, _) => RemoveCard(card);

        return shell;
    }

    void BuildRow(CardState card, HomeworkItem item)
    {
        var content = new TextBox
        {
            Text = item.Content ?? "",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 150,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            ToolTip = "作业内容（回车生效）",
        };

        var due = new ComboBox
        {
            Width = 210,
            Height = 32,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "提交时间：今天哪一节下课交 / 自定义 / 补交 / 不交",
        };

        var row = new RowState { Item = item, Content = content, Due = due };

        content.LostFocus += (_, _) => Commit(rebuild: false);
        content.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit(rebuild: false);
        };

        due.SelectionChanged += (_, _) => OnDueChanged(card, row);
        RefreshDueCombo(row);

        var remove = MiniButton("删除", "删除这一条作业");
        remove.Margin = new Thickness(0);
        remove.Click += (_, _) => RemoveRow(row);

        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = $"{card.Rows.Count + 1}.",
            FontSize = 12,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        Grid.SetColumn(number, 0);
        Grid.SetColumn(content, 1);
        Grid.SetColumn(due, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(number);
        grid.Children.Add(content);
        grid.Children.Add(due);
        grid.Children.Add(remove);

        card.Rows.Add(row);
        card.RowsHost.Children.Add(grid);
    }

    // ————————————————————— 提交时间 —————————————————————

    void RefreshDueCombo(RowState row)
    {
        row.Suppress = true;
        try
        {
            var kind = (row.Item.DueKind ?? "").Trim();
            var due = row.Item.DueTime;
            var dueEnd = row.Item.DueEndTime;

            row.Due.Items.Clear();
            foreach (var option in HomeworkDueOptions.Build(_schedule?.Engine, DateTime.Now, kind, due, dueEnd, row.Item.DueLabel))
                row.Due.Items.Add(option);

            row.Due.SelectedItem = HomeworkDueOptions.Find(row.Due.Items.OfType<HomeworkDueOption>(), kind, due);
        }
        finally
        {
            row.Suppress = false;
        }
    }

    void OnDueChanged(CardState card, RowState row)
    {
        if (row.Suppress) return;
        if (row.Due.SelectedItem is not HomeworkDueOption option) return;

        var owner = Window.GetWindow(this);

        // 「自定义时间…」：先警告后果，再填时间；取消就退回原来的选择
        if (option.IsPicker)
        {
            if (!HomeworkDueOptions.WarnCustomTime(owner) ||
                !HomeworkDueDialogs.TryPickTime(owner, row.Item.DueTime, out var picked))
            {
                RefreshDueCombo(row);
                return;
            }

            row.Item.DueKind = HomeworkDueKinds.Custom;
            row.Item.Due = HomeworkDue.ToTextPrecise(picked);
            row.Item.DueEnd = "";   // 自定义文本没有"下一节上课"，不构成提交时段
            row.Item.DueName = "";
            row.Item.DuePeriod = -1;
            row.Item.DueLabel = $"自定义文本 · {HomeworkDue.Describe(picked, DateTime.Now)}";
            row.Item.DueFired = "";
            row.Item.Date = picked.ToString("yyyy-MM-dd");

            RefreshDueCombo(row);
            Commit(rebuild: false);
            return;
        }

        if (option.Kind == HomeworkDueKinds.Late && !HomeworkDueOptions.WarnLate(owner))
        {
            RefreshDueCombo(row);
            return;
        }

        var previousDue = row.Item.Due ?? "";
        var previousEnd = row.Item.DueEnd ?? "";
        row.Item.DueKind = option.Kind;
        row.Item.Due = option.Due is null ? "" : HomeworkDue.ToTextPrecise(option.Due.Value);
        row.Item.DueEnd = option.WindowEnd is null ? "" : HomeworkDue.ToTextPrecise(option.WindowEnd.Value);
        row.Item.DueName = option.CourseName ?? "";
        row.Item.DuePeriod = option.PeriodIndex;
        row.Item.DueLabel = option.Text;

        if (option.Kind == HomeworkDueKinds.Late)
        {
            row.Item.Due = "";
            row.Item.DueEnd = "";
            row.Item.DueName = "";
            row.Item.DuePeriod = -1;
            row.Item.DueFired = "";
        }
        else if (!string.Equals(previousDue, row.Item.Due, StringComparison.Ordinal) ||
                 !string.Equals(previousEnd, row.Item.DueEnd, StringComparison.Ordinal))
        {
            // 时间或时段换了，"已到点"的旧标记就作废
            row.Item.DueFired = "";
            if (option.Due is not null) row.Item.Date = option.Due.Value.ToString("yyyy-MM-dd");
        }

        RefreshDueCombo(row);
        Commit(rebuild: false);
    }

    // ————————————————————— 落盘 —————————————————————

    static string Signature(IEnumerable<HomeworkItem> items) => string.Join("\n", items.Select(i =>
        $"{i.Subject}\u0001{i.Content}\u0001{i.Date}\u0001{i.Due}\u0001{i.DueEnd}\u0001{i.DueName}\u0001{i.DuePeriod}\u0001{i.DueKind}\u0001{i.DueLabel}"));

    /// <summary>把界面上的内容写回 homework.json（顺序 = 卡片顺序 → 行顺序）。内容没变就不写盘。</summary>
    void Commit(bool rebuild)
    {
        var ordered = new List<HomeworkItem>(_hw.Items.Count);
        foreach (var card in _cards)
        {
            var subject = (card.Subject.Text ?? "").Trim();
            foreach (var row in card.Rows)
            {
                row.Item.Subject = subject;
                row.Item.Content = (row.Content.Text ?? "").TrimEnd();
                ordered.Add(row.Item);
            }
        }

        var signature = Signature(ordered);
        if (!string.Equals(signature, _savedSignature, StringComparison.Ordinal))
        {
            _hw.ReplaceAll(ordered);
            _savedSignature = Signature(_hw.Items);
        }

        if (rebuild) Rebuild();
    }

    /// <summary>学科名输入框离开/回车：改了才重建（换颜色、必要时并到同名卡片），没改就只落盘。</summary>
    void CommitSubject(TextBox box, string known)
    {
        var now = (box.Text ?? "").Trim();
        if (string.Equals(now, (known ?? "").Trim(), StringComparison.Ordinal))
        {
            Commit(rebuild: false);
            return;
        }

        Commit(rebuild: false);
        Rebuild();
    }

    /// <summary>立刻写盘（平时失焦/选完时间就已自动保存，这里给一个明确的按钮）。</summary>
    void SaveNow()
    {
        Commit(rebuild: false);
        DialogLauncher.Toast(this, Controls.InfoBarSeverity.Success, "已保存", "作业已写入 homework.json");
    }

    // ————————————————————— 增删 —————————————————————

    void AddCard()
    {
        Commit(rebuild: false);
        _hw.Add(new HomeworkItem { Subject = "", Content = "", Date = "" });
        Rebuild();

        // 新卡片的学科框直接给焦点，省得用户再点一次
        if (_cards.Count > 0)
        {
            var box = _cards[^1].Subject;
            box.Focus();
            box.SelectAll();
        }
    }

    void AddRow(CardState card)
    {
        Commit(rebuild: false);

        var subject = (card.Subject.Text ?? "").Trim();
        var last = _hw.Items.FindLastIndex(i => string.Equals((i.Subject ?? "").Trim(), subject, StringComparison.Ordinal));
        var item = new HomeworkItem { Subject = subject, Content = "", Date = "" };

        if (last >= 0) _hw.Insert(last + 1, item);
        else _hw.Add(item);

        Rebuild();
    }

    void RemoveRow(RowState row)
    {
        Commit(rebuild: false);
        var index = _hw.Items.IndexOf(row.Item);
        if (index >= 0) _hw.RemoveAt(index);
        Rebuild();
    }

    void RemoveCard(CardState card)
    {
        var subject = (card.Subject.Text ?? "").Trim();
        var name = subject.Length == 0 ? "未分类" : subject;
        var count = _hw.Items.Count(i => string.Equals((i.Subject ?? "").Trim(), subject, StringComparison.Ordinal));

        // 用设置中心自带的确认浮层（不弹系统 MessageBox）
        DialogLauncher.Confirm(this, "移除科目",
            $"要删除「{name}」这个科目下的 {count} 条作业吗？",
            onOk: () =>
            {
                Commit(rebuild: false);

                var kept = new List<HomeworkItem>(_hw.Items);
                kept.RemoveAll(i => string.Equals((i.Subject ?? "").Trim(), subject, StringComparison.Ordinal));
                if (kept.Count != _hw.Items.Count) _hw.ReplaceAll(kept);

                Rebuild();
            },
            okText: "删除", cancelText: "取消");
    }

    // ————————————————————— 拖动排序 —————————————————————

    void BeginDrag(MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        var shell = FindShell(e.OriginalSource as DependencyObject);
        var card = shell is null ? null : _cards.FirstOrDefault(c => ReferenceEquals(c.Shell, shell));
        if (card is null) return;

        _dragCard = card;
        _dragStartY = e.GetPosition(_cardsHost).Y;
        _dragging = false;
        _cardsHost.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>触屏版本：手指按住卡片标题开始拖。接管 PreviewTouchDown，别让滚动吃掉。</summary>
    void BeginDragTouch(TouchEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        var shell = FindShell(e.OriginalSource as DependencyObject);
        var card = shell is null ? null : _cards.FirstOrDefault(c => ReferenceEquals(c.Shell, shell));
        if (card is null) return;

        _dragCard = card;
        _dragStartY = e.GetTouchPoint(_cardsHost).Position.Y;
        _dragging = false;
        _touchDevice = e.TouchDevice;
        e.TouchDevice.Capture(_cardsHost);
        e.Handled = true;
    }

    void OnCardsMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCard is null || _touchDevice is not null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        DragTo(e.GetPosition(_cardsHost).Y);
    }

    void OnCardsTouchMove(object sender, TouchEventArgs e)
    {
        if (_dragCard is null || _touchDevice != e.TouchDevice) return;

        DragTo(e.GetTouchPoint(_cardsHost).Position.Y);
        e.Handled = true;
    }

    void OnCardsTouchUp(object sender, TouchEventArgs e)
    {
        if (_touchDevice != e.TouchDevice) return;

        EndDrag();
        e.Handled = true;
    }

    /// <summary>鼠标 / 触屏共用的"拖到哪"：超过阈值才算拖动，目标位置变了就让卡片挪位。</summary>
    void DragTo(double y)
    {
        if (_dragCard is null) return;

        if (!_dragging)
        {
            if (Math.Abs(y - _dragStartY) < 6) return;
            _dragging = true;
            _dragCard.Shell.Opacity = 0.72;
        }

        var target = TargetIndex(y);
        var current = _cards.IndexOf(_dragCard);
        if (target < 0 || target == current || current < 0) return;

        _cards.RemoveAt(current);
        _cards.Insert(target, _dragCard);

        _cardsHost.Children.Remove(_dragCard.Shell);
        _cardsHost.Children.Insert(Math.Min(target, _cardsHost.Children.Count), _dragCard.Shell);
    }

    void EndDrag()
    {
        if (_dragCard is null || _endingDrag) return;

        _endingDrag = true;
        _dragCard.Shell.Opacity = 1;
        var moved = _dragging;

        _dragCard = null;
        _dragging = false;

        if (_cardsHost.IsMouseCaptured) _cardsHost.ReleaseMouseCapture();
        if (_touchDevice is not null)
        {
            _touchDevice.Capture(null);
            _touchDevice = null;
        }
        _endingDrag = false;

        // 顺序变了才落盘：只是点了一下标题不算
        if (moved) Commit(rebuild: false);
    }

    /// <summary>指针在"其它卡片"里的落点：落在谁的上半部分，就插到谁前面。</summary>
    int TargetIndex(double y)
    {
        var others = _cards.Where(c => c != _dragCard).ToList();
        for (var i = 0; i < others.Count; i++)
        {
            var top = others[i].Shell.TransformToAncestor(_cardsHost).Transform(new Point(0, 0)).Y;
            if (y < top + others[i].Shell.ActualHeight / 2) return i;
        }
        return others.Count;
    }

    static Border? FindShell(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Border border && border.Tag as string == "card") return border;
        }
        return null;
    }

    static bool IsInteractive(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase || d is TextBoxBase || d is ComboBox) return true;
            if (d is Border border && border.Tag as string == "card-header") return false;
        }
        return false;
    }

    // ————————————————————— 导入 / 导出 —————————————————————

    void ExportCsv()
    {
        Commit(rebuild: false);

        var dlg = new SaveFileDialog
        {
            Title = "导出作业 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"作业_{DateTime.Today:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("学科,日期,内容,提交时间");
        foreach (var i in _hw.Items)
            sb.Append(Csv(i.Subject)).Append(',')
              .Append(Csv(i.Date)).Append(',')
              .Append(Csv(i.Content)).Append(',')
              .AppendLine(Csv(i.Due));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 中文不乱码
    }

    void ExportJson()
    {
        Commit(rebuild: false);

        var dlg = new SaveFileDialog
        {
            Title = "导出作业 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"作业_{DateTime.Today:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_hw.Items, AtomicJson.Options), Encoding.UTF8);
    }

    void ImportJson()
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入作业 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var items = JsonSerializer.Deserialize<List<HomeworkItem>>(
                File.ReadAllText(dlg.FileName, Encoding.UTF8), AtomicJson.Options);
            if (items is null || items.Count == 0)
            {
                DialogLauncher.Toast(this, Controls.InfoBarSeverity.Warning, "导入作业", "文件里没有作业数据。");
                return;
            }
            _hw.ReplaceAll(items);
            Rebuild();
        }
        catch (Exception ex)
        {
            DialogLauncher.Toast(this, Controls.InfoBarSeverity.Error, "导入作业", "导入失败：" + ex.Message);
        }
    }

    static string Csv(string s)
    {
        s = (s ?? "").Trim();
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ————————————————————— 小工具 —————————————————————

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 科目 → 颜色：**先到先得**（新科目挑当前用得最少的那个色），
    /// 所以同屏科目不会撞色；已经定下来的颜色在拖动排序之后也不会变。
    /// （不用 string.GetHashCode：它每个进程都不一样，颜色会跳。）
    /// </summary>
    Color SubjectColor(string subject)
    {
        var name = (subject ?? "").Trim();
        if (name.Length == 0) return Color.FromRgb(0x8A, 0x8A, 0x8E);   // 未命名：中性灰
        if (_subjectColors.TryGetValue(name, out var cached)) return cached;

        var usage = new Dictionary<Color, int>();
        foreach (var used in _subjectColors.Values)
            usage[used] = usage.TryGetValue(used, out var n) ? n + 1 : 1;

        var pick = SubjectPalette[0];
        var best = int.MaxValue;
        foreach (var hex in SubjectPalette)
        {
            var candidate = (Color)ColorConverter.ConvertFromString(hex);
            var count = usage.TryGetValue(candidate, out var n) ? n : 0;
            if (count >= best) continue;
            best = count;
            pick = hex;
            if (best == 0) break;      // 有完全没用过的就用它
        }

        var color = (Color)ColorConverter.ConvertFromString(pick);
        _subjectColors[name] = color;
        return color;
    }

    Button Tool(string text, Action click, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            Style = Application.Current?.TryFindResource(accent ? "AccentButton" : "FluentButton") as Style,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 0,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
        };
        button.Click += (_, _) => click();
        return button;
    }

    static Button MiniButton(string text, string tip)
    {
        var button = new Button
        {
            Content = text,
            Style = Application.Current?.TryFindResource("FluentButton") as Style,
            MinWidth = 0,
            Height = 26,
            Padding = new Thickness(9, 0, 9, 0),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
        };
        return button;
    }
}
