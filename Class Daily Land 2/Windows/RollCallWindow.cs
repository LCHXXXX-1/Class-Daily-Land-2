using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassDailyLand.App.Theme;

namespace ClassDailyLand.App.Windows;

/// <summary>
/// 随机点名（对齐原版 randoms.py）：大号姓名 + 状态提示 + 「选名字文件 / 点名」两个按钮；
/// 同一人不连续重复（10 次冷却）。名单来自 name.json，也可另选 JSON 文件。
/// </summary>
public sealed class RollCallWindow : Window
{
    readonly TextBlock _name = new();
    readonly TextBlock _status = new();
    readonly Button _pick = new();
    readonly Button _open = new();
    readonly List<string> _names;
    readonly Queue<string> _recent = new();
    const int Cooldown = 10;

    public RollCallWindow(IReadOnlyList<string> names)
    {
        _names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();

        Title = "随机点名";
        Width = 520;
        Height = 360;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Border
        {
            CornerRadius = new CornerRadius(12),
        };
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _name.Text = "准备就绪";
        _name.FontSize = 48;
        _name.FontWeight = FontWeights.Bold;
        _name.HorizontalAlignment = HorizontalAlignment.Center;
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.TextAlignment = TextAlignment.Center;
        Grid.SetRow(_name, 0);

        _status.Text = "点「点名」开始";
        _status.FontSize = 12;
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.TextAlignment = TextAlignment.Center;
        _status.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_status, 1);

        _pick.Content = "点  名";
        _pick.Width = 120;
        _pick.Height = 44;
        _pick.Margin = new Thickness(8, 0, 8, 0);
        _pick.Click += (_, _) => Pick();

        _open.Content = "名单";
        _open.Width = 120;
        _open.Height = 44;
        _open.Margin = new Thickness(8, 0, 8, 0);
        _open.Click += (_, _) => OpenNames();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buttons.Children.Add(_open);
        buttons.Children.Add(_pick);
        Grid.SetRow(buttons, 2);

        grid.Children.Add(_name);
        grid.Children.Add(_status);
        grid.Children.Add(buttons);
        root.Child = grid;
        Content = root;

        ApplyTheme();
        Palette.Changed += OnPaletteChanged;
        // 点名窗是每次打开都新建的临时窗口：关闭时必须退订静态事件，
        // 否则 Palette.Changed 会一直持有它（连同整棵视觉树）造成泄漏。
        Closed += (_, _) => Palette.Changed -= OnPaletteChanged;

        if (_names.Count > 0) _status.Text = $"已载入 {_names.Count} 人";
    }

    void OnPaletteChanged(object? sender, EventArgs e) => ApplyTheme();

    void ApplyTheme()
    {
        if (Content is Border root) root.Background = Palette.WindowBackground;
        _name.Foreground = Palette.Text;
        _status.Foreground = Palette.TextDim;
        foreach (var b in new[] { _pick, _open })
        {
            b.Background = Palette.Card;
            b.BorderBrush = Palette.Border;
            b.Foreground = Palette.Text;
            b.BorderThickness = new Thickness(1);
        }
        _pick.Background = Palette.Accent;
        _pick.Foreground = Palette.AccentText;
        _pick.BorderThickness = new Thickness(0);
    }

    void OpenNames()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择点名名单（JSON 数组）",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var raw = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(raw);
            var loaded = (parsed ?? new()).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToList();
            if (loaded.Count == 0) { _status.Text = "该文件没有名字"; return; }
            _names.Clear();
            _names.AddRange(loaded);
            _recent.Clear();
            _status.Text = $"已载入 {_names.Count} 人";
        }
        catch (Exception ex)
        {
            _status.Text = "名单读取失败：" + ex.Message;
        }
    }

    void Pick()
    {
        if (_names.Count == 0) { _status.Text = "名单为空，请先设置 name.json"; return; }

        // 冷却：最近点过的人不进候选池（名单太小时自动放宽）
        var pool = _names.Where(n => !_recent.Contains(n)).ToList();
        if (pool.Count == 0) pool = _names.ToList();

        var pick = pool[Random.Shared.Next(pool.Count)];
        _recent.Enqueue(pick);
        while (_recent.Count > Math.Min(Cooldown, Math.Max(1, _names.Count - 1)))
            _recent.Dequeue();

        _name.Text = pick;
        _status.Text = $"共 {_names.Count} 人";
    }
}