using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace DeveloperIDE;

/// <summary>
/// 新建插件项目弹窗：插件名 / 作者 / 版本 / 保存路径。
///
/// 路径联动：<c>_pathAuto</c> 为真时，路径永远等于 <c>&lt;程序目录&gt;\plugins\&lt;插件名&gt;</c>，
/// 用户改名字这里就跟着改；一旦手动改过路径或点过「浏览」，就停止联动，别把人家选好的目录改跑。
/// </summary>
public partial class NewProjectWindow : Window
{
    /// <summary>插件名只允许英文字母与数字，且以字母开头（宿主还允许 . _ -，这里收紧免得命名空间出问题）。</summary>
    static readonly Regex NamePattern = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    static readonly Regex VersionPattern =
        new(@"^\d+(\.\d+){0,2}(-[0-9A-Za-z.-]{1,32})?$", RegexOptions.Compiled);

    /// <summary>用户没自己动过路径时才跟着插件名走。</summary>
    bool _pathAuto = true;

    /// <summary>程序内部改 PathBox 时别再触发一次「用户改过」的判断。</summary>
    bool _settingPath;

    public NewProjectWindow()
    {
        InitializeComponent();

        AuthorBox.Text = Environment.UserName;
        VersionBox.Text = "1.0.0";
        UpdatePath();

        Loaded += (_, _) => { NameBox.Focus(); RefreshState(); };
    }

    /// <summary>默认保存根：程序目录下的 plugins\（与主程序读插件的位置同名，装完就能扫到）。</summary>
    static string DefaultRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");

    // ─────────────────────────── 联动 ───────────────────────────

    void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_pathAuto) return;
        UpdatePath();
        RefreshState();
    }

    void OnPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingPath) return;
        _pathAuto = false;      // 手写过就别再跟着名字跑
        RefreshState();
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择插件项目要放在哪个父目录",
            Multiselect = false,
        };

        var seed = _pathAuto ? DefaultRoot : ParentOf(PathBox.Text);
        if (!Directory.Exists(seed))
        {
            // OpenFolderDialog 只能选现存的目录，默认根不存在就先建好，免得浏览框停在桌面
            try { Directory.CreateDirectory(DefaultRoot); } catch { /* 建不了就用当前目录 */ }
            seed = DefaultRoot;
        }
        if (Directory.Exists(seed)) dialog.InitialDirectory = seed;

        if (dialog.ShowDialog(this) != true) return;

        var picked = dialog.FolderName;

        // 选到 plugins 这一层 → 后面自动接插件名；选到更深的具体目录 → 就当项目目录本身用
        _pathAuto = string.Equals(Normalize(picked), Normalize(DefaultRoot), StringComparison.OrdinalIgnoreCase);
        SetPath(_pathAuto ? Combine(picked, NameBox.Text) : picked);
        RefreshState();
    }

    void UpdatePath() => SetPath(Combine(DefaultRoot, NameBox.Text));

    void SetPath(string path)
    {
        _settingPath = true;
        PathBox.Text = path;
        _settingPath = false;
    }

    // ─────────────────────────── 校验与提交 ───────────────────────────

    void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var dir)) return;

        try
        {
            Project = PluginWorkshop.CreateAt(dir, NameBox.Text.Trim(), AuthorBox.Text.Trim(), VersionBox.Text.Trim());
            DialogResult = true;      // true 之后再关窗，避免调用方拿到半成品
        }
        catch (Exception ex)
        {
            ShowError("创建失败：" + ex.Message);
        }
    }

    /// <summary>确定按钮给出的项目；取消时为 null。</summary>
    public PluginProject? Project { get; private set; }

    bool TryValidate(out string dir)
    {
        dir = "";
        var name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowError("插件名称不能为空。");
            NameBox.Focus();
            return false;
        }

        if (!NamePattern.IsMatch(name))
        {
            ShowError("插件名称只能用英文字母和数字，并且要以字母开头（它会当命名空间用）。");
            NameBox.Focus();
            return false;
        }

        var version = VersionBox.Text.Trim();
        if (!VersionPattern.IsMatch(version))
        {
            ShowError("版本号要用 1.0.0 这种三段式数字（可带 -beta.1 这种后缀）。");
            VersionBox.Focus();
            return false;
        }

        if (AuthorBox.Text.Trim().Length == 0)
        {
            ShowError("作者名字不能为空。");
            AuthorBox.Focus();
            return false;
        }

        // ★ 联动没跟上也要在这里重新拼一次，绝不能把 <插件名称> 这种占位写进路径
        dir = _pathAuto || string.IsNullOrWhiteSpace(PathBox.Text)
            ? Combine(DefaultRoot, name)
            : Path.GetFullPath(PathBox.Text.Trim());

        var probe = Path.Combine(dir, "plugin.json");
        if (File.Exists(probe) || File.Exists(Path.Combine(dir, "Plugin.cs")))
        {
            ShowError("这个目录里已经有插件项目了，换个名字或换个路径：\n" + dir);
            return false;
        }

        try
        {
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
            {
                ShowError("目标目录不是空的，为避免覆盖现有文件已停止：\n" + dir);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowError("读不了目标目录：" + ex.Message);
            return false;
        }

        ClearError();
        return true;
    }

    void RefreshState()
    {
        OkButton.IsEnabled = NamePattern.IsMatch(NameBox.Text.Trim());
        if (OkButton.IsEnabled) ClearError();
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    void ClearError() => ErrorText.Visibility = Visibility.Collapsed;

    static string Combine(string root, string name) => Path.Combine(root, name);

    static string ParentOf(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return "";
        }
    }
}
