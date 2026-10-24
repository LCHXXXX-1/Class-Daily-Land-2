using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ClassDailyLand.App.ViewModels;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Storage;
using Microsoft.Win32;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>
/// 作业编辑器（对位 web 版 buildHomeworkEditor）：学科 / 日期 / 内容 三列 + 行内操作。
/// 两种用法与原生一致：
///   内嵌进设置中心「作业」页 —— 改动即时生效、没有确认按钮；
///   独立「编辑作业」浮层 —— 带取消 / 确认，确认时才写回。
/// </summary>
public sealed class HomeworkEditor : UserControl
{
    sealed class Row
    {
        public required TextBox Subject { get; init; }
        public required TextBox Date { get; init; }
        public required TextBox Content { get; init; }
        public required FrameworkElement Shell { get; init; }
    }

    readonly HomeworkManager _hw;
    readonly bool _embedded;
    readonly StackPanel _rows = new();
    readonly List<Row> _items = new();
    readonly TextBlock _empty;

    public HomeworkEditor(SettingsViewModel vm, bool embedded)
    {
        _hw = vm.Ctx.Homework;
        _embedded = embedded;

        var root = new DockPanel();

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(Tool("新增一行", AddRow, "AccentButton"));
        toolbar.Children.Add(Tool("导出 CSV", ExportCsv));
        toolbar.Children.Add(Tool("导出 JSON", ExportJson));
        toolbar.Children.Add(Tool("导入 JSON", ImportJson));
        toolbar.Children.Add(Tool("打开作业文件夹", () => DialogLauncher.OpenFolder(vm.ConfigPath)));
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddHeaderCell(header, 0, "学科");
        AddHeaderCell(header, 1, "日期（可空）");
        AddHeaderCell(header, 2, "内容");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel();
        _empty = new TextBlock
        {
            Text = "还没有作业，点「新增一行」添加。",
            FontSize = 12,
            Margin = new Thickness(2, 8, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]!,
        };
        body.Children.Add(_rows);
        body.Children.Add(_empty);

        var scroll = new ScrollViewer
        {
            Content = body,
            MaxHeight = embedded ? 420 : double.PositiveInfinity,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(scroll);

        Content = root;
        Rebuild();
    }

    static void AddHeaderCell(Grid grid, int column, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]!,
        };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    Button Tool(string text, Action click, string styleKey = "FluentButton")
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources[styleKey]!,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 0,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
        };
        b.Click += (_, _) => click();
        return b;
    }

    // ————————————————————— 行 —————————————————————

    public void Rebuild()
    {
        _rows.Children.Clear();
        _items.Clear();
        _empty.Visibility = _hw.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < _hw.Items.Count; i++)
        {
            var index = i;
            var item = _hw.Items[i];
            _rows.Children.Add(BuildRow(index, item));
        }
    }

    FrameworkElement BuildRow(int index, HomeworkItem item)
    {
        var subject = Cell(item.Subject, 102);
        var date = Cell(item.Date, 102);
        var content = Cell(item.Content, 0);

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(subject, row, 0);
        Place(date, row, 1);
        Place(content, row, 2);

        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Tool("↑", () => Move(index, -1)));
        controls.Children.Add(Tool("↓", () => Move(index, 1)));
        controls.Children.Add(Tool("删除", () => Remove(index)));
        Place(controls, row, 3);

        if (_embedded)
        {
            subject.LostFocus += (_, _) => Commit();
            date.LostFocus += (_, _) => Commit();
            content.LostFocus += (_, _) => Commit();
        }

        _items.Add(new Row { Subject = subject, Date = date, Content = content, Shell = row });
        return row;
    }

    static void Place(UIElement element, Grid grid, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    static TextBox Cell(string text, double width)
    {
        var box = new TextBox
        {
            Text = text,
            Style = (Style)Application.Current.Resources["EdInput"]!,
            Margin = new Thickness(0, 0, 8, 0),
        };
        if (width > 0) box.Width = width;
        else box.HorizontalAlignment = HorizontalAlignment.Stretch;
        return box;
    }

    List<HomeworkItem> Snapshot()
    {
        var items = new List<HomeworkItem>(_items.Count);
        foreach (var r in _items)
            items.Add(new HomeworkItem
            {
                Subject = r.Subject.Text.Trim(),
                Date = r.Date.Text.Trim(),
                Content = r.Content.Text,
            });
        return items;
    }

    /// <summary>把当前输入写回 homework.json（内嵌模式在失焦时调用）。</summary>
    public void Commit() => _hw.ReplaceAll(Snapshot());

    void AddRow()
    {
        if (_embedded) Commit();
        var last = _hw.Items.Count == 0 ? null : _hw.Items[^1];
        _hw.Add(new HomeworkItem { Subject = last?.Subject ?? "", Date = "" });
        Rebuild();
    }

    void Remove(int index)
    {
        if (_embedded) Commit();
        if (index < 0 || index >= _hw.Items.Count) return;
        _hw.RemoveAt(index);
        Rebuild();
    }

    void Move(int index, int delta)
    {
        if (_embedded) Commit();
        var target = index + delta;
        if (target < 0 || target >= _hw.Items.Count) return;
        _hw.Move(index, target);
        Rebuild();
    }

    // ————————————————————— 导入 / 导出 —————————————————————

    void ExportCsv()
    {
        CommitIfNeeded();
        var dlg = new SaveFileDialog
        {
            Title = "导出作业 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"作业_{DateTime.Today:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("学科,日期,内容");
        foreach (var i in _hw.Items)
            sb.Append(Csv(i.Subject)).Append(',').Append(Csv(i.Date)).Append(',').AppendLine(Csv(i.Content));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 中文不乱码
    }

    void ExportJson()
    {
        CommitIfNeeded();
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

    void CommitIfNeeded() { if (_embedded) Commit(); }

    static string Csv(string s)
    {
        s = (s ?? "").Trim();
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}

/// <summary>独立的「编辑作业」浮层：带取消 / 确认（对位 web 版 openHomeworkEditor）。</summary>
public sealed class HomeworkDialog : DialogPanel
{
    public HomeworkDialog(SettingsViewModel vm) : base(vm, "编辑作业",
        "改完点「保存」写回；取消则丢弃本次改动。", 760)
    {
        var backup = vm.Ctx.Homework.Items
            .Select(i => new HomeworkItem { Subject = i.Subject, Date = i.Date, Content = i.Content })
            .ToList();

        var editor = new HomeworkEditor(vm, embedded: false);
        Add(editor);

        AddButton("取消", () =>
        {
            vm.Ctx.Homework.ReplaceAll(backup);
            Close();
        });
        AddButton("保存", () =>
        {
            editor.Commit();
            Close();
        }, primary: true);
    }
}
