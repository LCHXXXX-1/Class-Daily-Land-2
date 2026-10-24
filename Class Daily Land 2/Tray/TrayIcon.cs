using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinForms = System.Windows.Forms;
using ClassDailyLand.Core;

namespace ClassDailyLand.App.Tray;

/// <summary>
/// 托盘：左键开设置，右键弹出 WPF 原生菜单（Win11 样式）。
/// WPF 没有原生托盘，用框架自带的 WinForms NotifyIcon（不引第三方库）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _notify;
    readonly Logger? _log;

    bool _mainChecked;
    bool _islandChecked;
    bool _subChecked;

    public event Action<bool>? MainToggled;
    public event Action<bool>? IslandToggled;
    public event Action<bool>? SubToggled;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon(Logger? log = null)
    {
        _log = log;
        _notify = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "班级日常 ClassDailyLand",
            Visible = true,
        };

        _notify.MouseUp += OnMouseUp;
    }

    void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            SettingsRequested?.Invoke();
            return;
        }

        if (e.Button == WinForms.MouseButtons.Right)
            ShowMenu();
    }

    void ShowMenu()
    {
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.Invoke(() =>
        {
            var menu = BuildMenu();
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        });
    }

    ContextMenu BuildMenu()
    {
        var miMain = new MenuItem
        {
            Header = "显示主窗口",
            IsCheckable = true,
            IsChecked = _mainChecked,
        };
        miMain.Checked += (_, _) =>
        {
            _mainChecked = true;
            MainToggled?.Invoke(true);
        };
        miMain.Unchecked += (_, _) =>
        {
            _mainChecked = false;
            MainToggled?.Invoke(false);
        };

        var miIsland = new MenuItem
        {
            Header = "显示灵动岛",
            IsCheckable = true,
            IsChecked = _islandChecked,
        };
        miIsland.Checked += (_, _) =>
        {
            _islandChecked = true;
            IslandToggled?.Invoke(true);
        };
        miIsland.Unchecked += (_, _) =>
        {
            _islandChecked = false;
            IslandToggled?.Invoke(false);
        };

        var miSub = new MenuItem
        {
            Header = "显示副岛",
            IsCheckable = true,
            IsChecked = _subChecked,
        };
        miSub.Checked += (_, _) =>
        {
            _subChecked = true;
            SubToggled?.Invoke(true);
        };
        miSub.Unchecked += (_, _) =>
        {
            _subChecked = false;
            SubToggled?.Invoke(false);
        };

        var miSettings = new MenuItem { Header = "设置…" };
        miSettings.Click += (_, _) => SettingsRequested?.Invoke();

        var miExit = new MenuItem { Header = "退出" };
        miExit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(miMain);
        menu.Items.Add(miIsland);
        menu.Items.Add(miSub);
        menu.Items.Add(new Separator());
        menu.Items.Add(miSettings);
        menu.Items.Add(miExit);
        return menu;
    }

    static Icon LoadIcon()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(path)) return new Icon(path);
        }
        catch { /* 回退系统图标 */ }
        return System.Drawing.SystemIcons.Application;
    }

    public void SetChecks(bool main, bool island, bool sub)
    {
        _mainChecked = main;
        _islandChecked = island;
        _subChecked = sub;
    }

    public void Balloon(string title, string text, int seconds = 3)
    {
        try
        {
            _notify.ShowBalloonTip(seconds * 1000, title, text, WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log?.Warn("托盘气泡失败: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        catch { }
    }
}
