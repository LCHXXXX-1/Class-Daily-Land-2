using System;
using System.Windows;
using System.Windows.Controls;

namespace DeveloperIDE;

/// <summary>IDE 设置：下拉框切换主题，改动即时生效并写进 ide_config.json。</summary>
public partial class SettingsWindow : Window
{
    /// <summary>构造里选中的那一项会触发 SelectionChanged；初始化完再放行。</summary>
    bool _ready;

    /// <summary>由外部（别的窗口切了主题）回灌下拉框时，不要再触发一次 ApplyTheme。</summary>
    bool _suppress;

    public SettingsWindow()
    {
        InitializeComponent();

        ThemeCombo.SelectedIndex = App.IsDark ? 0 : 1;
        UpdateHint();

        App.ThemeChanged += OnAppThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnAppThemeChanged;

        _ready = true;
    }

    void OnAppThemeChanged() => Dispatcher.BeginInvoke(new Action(SyncCombo));

    void SyncCombo()
    {
        var want = App.IsDark ? 0 : 1;
        if (ThemeCombo.SelectedIndex == want) { UpdateHint(); return; }

        _suppress = true;
        ThemeCombo.SelectedIndex = want;
        _suppress = false;
        UpdateHint();
    }

    void OnThemeSelection(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress) return;

        var dark = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "dark";
        if (dark != App.IsDark) App.ApplyTheme(dark);
        UpdateHint();
    }

    void UpdateHint() =>
        HintText.Text = "当前：" + (App.IsDark ? "深色主题" : "浅色主题") + "，选择会记住（ide_config.json）。";
}
