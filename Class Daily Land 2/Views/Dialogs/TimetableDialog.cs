using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassDailyLand.Core.Models;
using Microsoft.Win32;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>
/// 课表管理（对位 web 版 openTimetable）：左侧课表列表（双击启用）+ 右侧节次与课程格。
/// ★ 只编辑周一~周五：周末课程存在 schedule.json 的 weekend 里，走「周末作息」浮层，
///   早期版本硬塞 7 天会导致「课表填了周末课、灵动岛还是今日无课」。
/// </summary>
public sealed class TimetableDialog : DialogPanel
{
    const int Days = 5;
    static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五" };

    readonly ListBox _list = new() { Width = 170, Height = 300, Style = S("EdList") };
    readonly StackPanel _periods = new();
    readonly Grid _grid = new();
    readonly TextBlock _status = new() { Style = S("EdEmpty"), Margin = new Thickness(0, 8, 0, 0) };

    string _current = "";

    public TimetableDialog(ViewModels.SettingsViewModel vm) : base(vm, "课表管理",
        "切换、备份与导入导出课表；改完焦点离开即保存。", 900)
    {
        _list.MouseDoubleClick += (_, _) => ActivateSelected();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string name && name != _current) { _current = name; RebuildDetail(); }
        };

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "课表", Style = S("EdFieldLabel"), Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(_list);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Width = 170 };
        tools.Children.Add(Tiny("新建", NewTimetable));
        tools.Children.Add(Tiny("复制新建", CopyCurrent));
        tools.Children.Add(Tiny("重命名", RenameCurrent));
        tools.Children.Add(Tiny("启用", ActivateSelected));
        tools.Children.Add(Tiny("删除", DeleteCurrent));
        left.Children.Add(tools);

        var io = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Width = 170 };
        io.Children.Add(Tiny("导出 JSON", ExportJson));
        io.Children.Add(Tiny("导入 JSON", ImportJson));
        left.Children.Add(io);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Card("节次时间", "名称与起止时间；结束早于开始视为跨午夜", _periods));
        right.Children.Add(Tiny("＋ 加一节", AddPeriod));
        right.Children.Add(Card("课程表", "只编辑周一到周五，周末走「周末作息」", _grid));
        right.Children.Add(_status);

        var split = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(left, Dock.Left);
        split.Children.Add(left);
        split.Children.Add(new ScrollViewer
        {
            Content = right,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        Add(split);
        AddButton("关闭", Close);

        RebuildList();
    }

    Button Tiny(string text, Action click)
    {
        var b = SmallButton(text, click);
        b.Margin = new Thickness(0, 0, 6, 6);
        return b;
    }

    // ————————————————————— 列表 —————————————————————

    void RebuildList()
    {
        var names = Ctx.Schedule.TimetableNames;
        _current = Ctx.Schedule.File.Active;
        _list.Items.Clear();
        foreach (var n in names)
            _list.Items.Add(n == Ctx.Schedule.File.Active ? "● " + n : n);
        var idx = names.ToList().IndexOf(_current);
        if (idx >= 0) _list.SelectedIndex = idx;
        RebuildDetail();
    }

    string SelectedName()
    {
        var raw = _list.SelectedItem as string;
        if (raw is null) return _current;
        return raw.StartsWith("● ", StringComparison.Ordinal) ? raw[2..] : raw;
    }

    void ActivateSelected()
    {
        var name = SelectedName();
        if (name.Length == 0) return;
        Ctx.Schedule.SetActive(name);
        RebuildList();
    }

    void NewTimetable()
    {
        var name = AskText("新建课表", "新课表名：", $"课表{Ctx.Schedule.TimetableNames.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!Ctx.Schedule.CreateTimetable(name.Trim()))
            Tell(this, "这个名字已存在。", "新建课表");
        RebuildList();
    }

    void CopyCurrent()
    {
        var name = AskText("复制当前课表", "新课表名（内容复制自「" + SelectedName() + "」）：",
            SelectedName() + " 副本");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!Ctx.Schedule.CreateTimetable(name.Trim(), Ctx.Schedule.File.Active))
            Tell(this, "这个名字已存在。", "复制新建");
        RebuildList();
    }

    void RenameCurrent()
    {
        var old = SelectedName();
        var name = AskText("重命名课表", "新名字：", old);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == old) return;
        if (!Ctx.Schedule.RenameTimetable(old, name.Trim()))
            Tell(this, "重命名失败（名字可能重复）。", "重命名");
        RebuildList();
    }

    void DeleteCurrent()
    {
        var name = SelectedName();
        if (Ctx.Schedule.TimetableNames.Count <= 1)
        {
            Tell(this, "至少保留一张课表。", "删除");
            return;
        }
        if (!AskConfirm($"确定删除课表「{name}」吗？")) return;
        Ctx.Schedule.DeleteTimetable(name);
        RebuildList();
    }

    // ————————————————————— 明细 —————————————————————

    void RebuildDetail()
    {
        RebuildPeriods();
        RebuildGrid();
        _status.Text = $"使用中：{Ctx.Schedule.File.Active}（{Ctx.Schedule.Active.Periods.Count} 节 × {Days} 天）";
    }

    void RebuildPeriods()
    {
        _periods.Children.Clear();
        var table = Table();

        for (var i = 0; i < table.Periods.Count; i++)
        {
            var index = i;
            var p = table.Periods[i];
            var name = Input(p.Name, 100);
            var start = TimeInput(p.Start);
            var end = TimeInput(p.End);

            void Save()
            {
                if (index >= table.Periods.Count) return;
                table.Periods[index].Name = name.Text.Trim();
                table.Periods[index].Start = Norm(start.Text);
                table.Periods[index].End = Norm(end.Text);
                Ctx.Schedule.Save();
            }
            name.LostFocus += (_, _) => Save();
            start.LostFocus += (_, _) => Save();
            end.LostFocus += (_, _) => Save();

            _periods.Children.Add(Row(
                new TextBlock { Text = $"{i + 1}.", Style = S("EdCellLabel"), Width = 22 },
                name, start, end,
                Tiny("删除", () =>
                {
                    var kept = table.Periods.Where((_, k) => k != index).ToList();
                    table.Periods = kept;
                    table.TrimPeriods(kept.Count);
                    Ctx.Schedule.Save();
                    RebuildDetail();
                })));
        }

        if (table.Periods.Count == 0) _periods.Children.Add(Empty("还没有节次，点下方「＋ 加一节」。"));
    }

    void AddPeriod()
    {
        var table = Table();
        table.Periods.Add(new Period($"第{table.Periods.Count + 1}节", "08:00", "08:45"));
        Ctx.Schedule.Save();
        RebuildDetail();
    }

    void RebuildGrid()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();

        var table = Table();
        const double cellWidth = 96;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        for (var d = 0; d < Days; d++)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < table.Periods.Count; i++)
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var d = 0; d < Days; d++)
        {
            var head = new TextBlock
            {
                Text = DayNames[d],
                Style = S("EdCellLabel"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetColumn(head, d + 1);
            _grid.Children.Add(head);
        }

        for (var i = 0; i < table.Periods.Count; i++)
        {
            var pi = i;
            var label = new TextBlock
            {
                Text = $"{i + 1}. {(table.Periods[i].Name ?? "").Trim()}",
                Style = S("EdRowLabel"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(label, i + 1);
            Grid.SetColumn(label, 0);
            _grid.Children.Add(label);

            for (var d = 0; d < Days; d++)
            {
                var day = d;
                var cell = Input(table.CourseAt(pi, day), cellWidth - 8);
                cell.Style = S("EdCellInput");
                cell.Margin = new Thickness(2);
                cell.LostFocus += (_, _) =>
                {
                    table.SetCourse(pi, day, cell.Text.Trim());
                    Ctx.Schedule.Save();
                };
                Grid.SetRow(cell, i + 1);
                Grid.SetColumn(cell, d + 1);
                _grid.Children.Add(cell);
            }
        }

        if (table.Periods.Count == 0) _grid.Children.Add(Empty("先加几节课，再填课程。"));
    }

    // ————————————————————— 导入 / 导出 —————————————————————

    void ExportJson()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"课表_{DateTime.Today:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        System.IO.File.WriteAllText(dlg.FileName, Ctx.Schedule.ExportJson(), System.Text.Encoding.UTF8);
        _status.Text = "已导出：" + dlg.FileName;
    }

    void ImportJson()
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!AskConfirm("导入会覆盖当前全部课表（含假期与调休），确定继续吗？")) return;
        try
        {
            if (!Ctx.Schedule.ImportJson(System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8)))
                Tell(this, "文件内容不是有效的课表 JSON。", "导入课表");
            else _status.Text = "导入完成。";
        }
        catch (Exception ex)
        {
            Tell(this, "导入失败：" + ex.Message, "导入课表");
        }
        RebuildList();
    }

    static string Norm(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 4 && s[1] == ':') return "0" + s;
        return s;
    }

    // ————————————————————— 输入小窗（新建 / 重命名用） —————————————————————

    string? AskText(string title, string hint, string initial)
    {
        var input = Input(initial, 240);
        string? result = null;

        var ok = new Button { Content = "确定", Width = 76, Height = 28, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 76, Height = 28, IsCancel = true };
        ok.Style = (Style)Application.Current.Resources["AccentButton"]!;

        var box = new PromptWindow(title, hint, input, ok, cancel)
        {
            Owner = Window.GetWindow(this),
        };
        ok.Click += (_, _) => { result = input.Text; box.Close(); };
        cancel.Click += (_, _) => box.Close();
        box.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>取当前编辑的课表（按名字，取不到则回落到使用中那张）。</summary>
    Core.Models.Timetable Table() =>
        Ctx.Schedule.File.Timetables.TryGetValue(_current, out var t) ? t : Ctx.Schedule.Active;
}

/// <summary>只用来收一个名字的小窗（无边框，跟设置窗同一套皮肤）。</summary>
sealed class PromptWindow : Window
{
    public PromptWindow(string title, string hint, UIElement input, UIElement ok, UIElement cancel)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 16, 20, 16),
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        border.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var stack = new StackPanel();
        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var hintBlock = new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        stack.Children.Add(head);
        stack.Children.Add(hintBlock);
        stack.Children.Add(input);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(new FrameworkElement { Width = 8 });
        row.Children.Add(ok);
        stack.Children.Add(row);

        border.Child = stack;
        Content = border;
        MouseLeftButtonDown += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove();
        };
    }
}
