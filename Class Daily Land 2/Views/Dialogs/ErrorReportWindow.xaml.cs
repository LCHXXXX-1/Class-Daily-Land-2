using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Diagnostics;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 出错时的统一提示窗：无边框卡片，标题「班级日常 · 出错了」。
///
/// 只说三件事：出错了、别发截图、是哪个模块出错的（点「查看详情」看完整堆栈）。
/// 导出的日志文件里有全部上下文 —— 截图里没有。
///
/// 同一时刻只允许一个（见 <see cref="Report"/>）：错误往往是连串发生的，
/// 弹五个一样的窗只会把用户彻底堵死。
/// </summary>
public partial class ErrorReportWindow : Window
{
    static ErrorReportWindow? _current;

    /// <summary>
    /// 每次报错都会触发（错误项, 出错模块）。托盘据此冒一条 Windows 通知 ——
    /// 窗口可能被挡住或在别的虚拟桌面，右下角那条更容易被注意到。
    /// </summary>
    public static event Action<string, string>? Reported;

    readonly string _kind;
    readonly string _module;
    readonly string _detail;
    readonly ErrorCode? _code;
    readonly ErrorPayload? _payload;   // 非空 = 运行在报信进程里，恢复操作要自己干
    string? _snapshot;

    /// <summary>主进程模式下的恢复操作（由 AppController 注入：重启 / 重装走主程序的完整流程）。</summary>
    public static Func<bool>? RestartHook;
    public static Func<bool>? ReinstallHook;

    ErrorReportWindow(string kind, string module, string detail, string? snapshot, ErrorCode? code, ErrorPayload? payload = null)
    {
        InitializeComponent();

        _kind = kind;
        _module = module;
        _detail = detail;
        _snapshot = snapshot;
        _code = code;
        _payload = payload;

        // 标题跟着场景走：异常退出 ≠ 出错，措辞不该吓唬人也不该含糊
        var scene = code?.Code == ErrorCodes.UnexpectedExit.Code || kind.Contains("异常退出", StringComparison.Ordinal)
            ? "异常退出"
            : "出错啦";
        Title = $"Class Daily Land 2 · {scene}";
        TitleText.Text = Title;
        ModuleText.Text = "出错模块：" + module;
        CodeText.Text = code is null
            ? "报错码：未登记"
            : $"报错码：{code.Code} · {code.Name}";
        SnapshotText.Text = snapshot is null ? "" : "日志已自动保存";
        DetailText.Text = BuildDetailText();

        // 无边框窗口没有系统快捷键：Esc 关闭要自己接
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    string BuildDetailText()
    {
        var sb = new StringBuilder();

        // ★ 码只代表"失败的类别"，不代表本次的具体原因。
        //   同一个码可能在不同分支被抛出（例如插件安装失败可能是解压、校验或写盘），
        //   所以「归类/说明」写码表的定义，「本次原因」单独一行写这一次的真实消息。
        sb.AppendLine($"报错码　{(_code?.Code ?? "未登记")}");
        if (_code is not null)
        {
            sb.AppendLine($"归类　　{_code.Name}");
            sb.AppendLine($"说明　　{_code.Meaning}");
        }

        sb.AppendLine($"本次原因　{FirstLine(_detail)}");
        sb.AppendLine($"模块　　{_module}");
        sb.AppendLine($"错误项　{_kind}");
        sb.AppendLine($"时间　　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (_snapshot is not null) sb.AppendLine($"日志文件　{_snapshot}");
        sb.AppendLine();
        sb.Append(_detail);
        return sb.ToString().TrimEnd();
    }

    /// <summary>取多行文本的第一行非空内容（异常消息通常在首行）。</summary>
    static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.TrimEnd('\r');
        }
        return "（没有更多信息）";
    }

    /// <summary>
    /// 圆角/阴影交给 DWM：Win11 上给无边框窗口显式要 ROUND_CORNER，
    /// 系统会按自己的半径裁切并补阴影 —— 比自绘圆角更像原生窗口，也不会在缩放时糊边。
    /// 老系统上这个调用直接失败（返回非 0），窗口就是直角，不影响功能。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // 拿不到 DWM（老系统 / 远程桌面）：保持直角即可
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>估算折行后的行数：中文按 2 个宽度单位算（等宽字体下才不低估）。</summary>
    static int VisualLineCount(string text)
    {
        const double unitsPerLine = 62;      // 约等于详情框可用宽度 / 单字符宽度
        var lines = 0;

        foreach (var raw in text.Split('\n'))
        {
            var width = 0;
            foreach (var ch in raw.TrimEnd('\r'))
                width += ch >= 0x2E80 ? 2 : 1;   // CJK 及全角标点占两格

            lines += Math.Max(1, (int)Math.Ceiling(width / unitsPerLine));
        }

        return lines;
    }

    /// <summary>无边框窗口只能拖标题条（不挂在正文上，免得选文字时把窗口拖走）。</summary>
    void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 拖动失败无所谓 */ }
    }

    void OnToggleDetail(object sender, RoutedEventArgs e)
    {
        var show = DetailPanel.Visibility != Visibility.Visible;

        if (show)
        {
            // 展开：给窗口一个固定高度，详情行吃满剩余空间；内容超出时由 TextBox 自己滚动。
            // ★ 只改 Visibility、靠 SizeToContent 自动长高是**不行**的：窗口在运行时不会重新
            //   测量到新高度，实测被压成五六十像素，日志当场被切掉一半（用户截图就是这样）。
            DetailPanel.Visibility = Visibility.Visible;
            DetailRow.Height = new GridLength(1, GridUnitType.Star);
            SizeToContent = SizeToContent.Manual;

            // 按「折行后的可视行数」估算高度：长行不少，只数 \n 会估小，结果是刚展开就出滚动条。
            // ★ 必须用 DetailText.Text（含报错码/含义那几行），不能只数异常正文 ——
            //   含义那一行很长，会折成三四行，漏算就又切底（踩过）。
            var visualLines = VisualLineCount(DetailText.Text ?? "");

            var textHeight = Math.Min(470, visualLines * 17 + 40);
            var target = 108 + textHeight + 58;     // 标题条 + 提示区 + 详情 + 底部按钮
            Height = Math.Clamp(target, 430, MaxHeight);

            DetailToggle.Content = "收起详情";
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailRow.Height = GridLength.Auto;
            SizeToContent = SizeToContent.Height;
            Height = double.NaN;          // 交回给 SizeToContent

            DetailToggle.Content = "查看详情";
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void OnExportClick(object sender, RoutedEventArgs e)
    {
        var log = Logger.Current;

        // 快照可能是空（比如日志还没就绪）：点了导出再存一次，别让按钮变成死键
        _snapshot ??= log?.SaveSnapshot(_kind, 200);

        if (_snapshot is null || !File.Exists(_snapshot))
        {
            MessageBox.Show(this, "日志快照没能写出来（日志目录不可写）。", "导出日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出日志",
            FileName = $"ClassDailyLand2-{_kind}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.Copy(_snapshot, dialog.FileName, overwrite: true);
            SnapshotText.Text = "已导出 " + Path.GetFileName(dialog.FileName);
            MessageBox.Show(this, "日志已导出：\n" + dialog.FileName, "导出日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出日志",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 统一的报错入口：写 ERROR 日志 → 存最近 200 行快照 → 发通知 → 弹窗（同一时刻只留一个）。
    /// 任意线程可调用。<paramref name="detail"/> 同时进日志和窗口，别只给窗口。
    /// </summary>
    public static void Report(string kind, string? detail = null, Exception? exception = null,
        string? moduleName = null, ErrorCode? code = null)
    {
        var logger = Logger.Current;

        // 错误码优先级：调用方显式指定 > 异常自带的码 > 按异常类型粗分类 > 兜底
        var resolved = code ?? Classify(exception);

        var text = detail ?? exception?.Message ?? "未知错误";
        if (exception is not null && detail is not null)
            text += Environment.NewLine + exception;

        var module = string.IsNullOrWhiteSpace(moduleName) ? ModuleOf(exception) : moduleName!;

        try { logger?.Error($"[报错] {resolved.Code} {kind}（{module}）：{text}"); }
        catch { /* 日志失败不能影响弹窗 */ }

        string? snapshot = null;
        try { snapshot = logger?.SaveSnapshot($"{resolved.Code}-{kind}", 200, $"{module}｜{text}"); }
        catch { /* 快照失败就用窗口兜底 */ }

        // 托盘通知先发：即使后面弹窗因故失败，用户也还能看到"出事了，去日志页看"
        try { Reported?.Invoke(kind, module); } catch { /* 通知失败不影响主流程 */ }

        // ★ 先在磁盘上把报告写死，再交给**独立进程**去显示：
        //   主程序此时可能已经处于"随时会挂"的状态，窗口开在本进程里就一起没了。
        var payloadPath = WritePayload(kind, module, text, resolved, snapshot);
        if (payloadPath is not null && TrySpawnReporter(payloadPath)) return;

        // 进程拉不起来（被安全软件拦、路径异常等）就在本进程里弹，功能不能因此消失
        ShowInProcess(kind, module, text, snapshot, resolved);
    }

    /// <summary>在本进程内弹窗（独立进程失败时的兜底，也是 --error-report 模式的实现）。</summary>
    static void ShowInProcess(string kind, string module, string text, string? snapshot, ErrorCode? code)
    {
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_current is { IsLoaded: true })
                {
                    _current.Activate();
                    return;
                }

                var window = new ErrorReportWindow(kind, module, text, snapshot, code);
                if (app.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
                    window.Owner = owner;

                _current = window;
                window.Closed += (_, _) => _current = null;
                window.Show();
                window.Activate();
            }
            catch
            {
                // 连弹窗都失败：至少别让异常冒泡（那会变成死循环）
            }
        }));
    }

    // ————————————————————— 恢复操作 —————————————————————

    /// <summary>
    /// 重启软件：主进程模式走 AppController（完整收尾）；报信进程模式自己来 ——
    /// 结束主程序（如果还活着）→ 清运行标记（防误报 ESY4003）→ 启动新实例。
    /// </summary>
    void OnRestartAppClick(object sender, RoutedEventArgs e)
    {
        if (_payload is null)
        {
            if (RestartHook?.Invoke() != true) return;
            Close();
            return;
        }

        try
        {
            TerminateMain(_payload);
            try { File.Delete(Path.Combine(_payload.DataRoot, ".running.json")); } catch { }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重启失败：" + ex.Message, "重启软件",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Close();
    }

    /// <summary>
    /// 重新安装最新版本：报信进程不重建更新服务的上下文，直接拉起
    /// <c>--reinstall-latest</c> 无头模式（同一个 exe，独立于主程序），然后关掉自己。
    /// </summary>
    void OnReinstallAppClick(object sender, RoutedEventArgs e)
    {
        if (_payload is null)
        {
            if (ReinstallHook?.Invoke() != true) return;
            Close();
            return;
        }

        try
        {
            var exe = Environment.ProcessPath!;
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--reinstall-latest");
            info.ArgumentList.Add(_payload.DataRoot);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动重新安装失败：" + ex.Message, "重新安装最新版本",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 主程序马上会被更新器关掉，这个窗口的使命完成
        Close();
    }

    /// <summary>结束主程序：先礼貌请求关闭，3 秒还没走就强杀。</summary>
    static void TerminateMain(ErrorPayload payload)
    {
        if (payload.MainPid <= 0 || payload.MainPid == Environment.ProcessId) return;

        try
        {
            var main = Process.GetProcessById(payload.MainPid);
            if (main.HasExited) return;

            main.CloseMainWindow();
            if (!main.WaitForExit(3000))
                main.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { /* 进程已经没了，正好 */ }
        catch (Exception) { /* 强杀失败就算了，用户自己会关 */ }
    }

    // ————————————————————— 独立报错进程 —————————————————————

    /// <summary>报告落盘路径（logs\report-时间戳.json）。写不出来返回 null。</summary>
    static string? WritePayload(string kind, string module, string text, ErrorCode code, string? snapshot)
    {
        try
        {
            var log = Logger.Current;
            var dir = snapshot is null ? null : Path.GetDirectoryName(snapshot);
            if (string.IsNullOrEmpty(dir))
                dir = log?.LogDirectory;
            if (string.IsNullOrEmpty(dir)) return null;

            var payload = new ErrorPayload
            {
                Kind = kind,
                Module = module,
                Code = code.Code,
                CodeName = code.Name,
                CodeMeaning = code.Meaning,
                Level = code.Level,
                Detail = text,
                Snapshot = snapshot,
                MainPid = Environment.ProcessId,
                DataRoot = log?.DataRoot ?? "",
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Version = Logger.AppVersion,
            };

            var path = Path.Combine(dir, $"report-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉起一个只负责显示报告的自身副本（<c>--error-report &lt;json&gt;</c>）。
    /// 主程序随后崩溃也不影响这个窗口和已保存的日志。
    /// </summary>
    static bool TrySpawnReporter(string payloadPath)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;

            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--error-report");
            info.ArgumentList.Add(payloadPath);
            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>--error-report &lt;json&gt;</c> 入口：读报告 → 只显示这一个窗口 → 退出。
    /// 不启动 AppController、不碰单实例锁（它是"来报信的"，不是第二个主程序）。
    /// </summary>
    public static void RunReporter(string payloadPath)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<ErrorPayload>(File.ReadAllText(payloadPath, Encoding.UTF8));
            if (payload is null) return;

            var code = ErrorCodes.Find(payload.Code)
                       ?? new ErrorCode(payload.Code, payload.CodeName, payload.CodeMeaning, payload.Level);

            var window = new ErrorReportWindow(payload.Kind, payload.Module, payload.Detail, payload.Snapshot, code, payload);
            window.ShowDialog();
        }
        catch
        {
            // 报信进程自己出错就静默退出，不要套娃
        }
    }

    /// <summary>
    /// 没带码的异常按类型粗分类。**只认语义明确的几种**，其余一律兜底 ——
    /// 宁可显示"未预期异常"，也不要为了好看硬套一个不相干的具体码。
    /// </summary>
    static ErrorCode Classify(Exception? exception) => exception switch
    {
        null => ErrorCodes.Unknown,
        AppException app => app.Code,
        FileNotFoundException => ErrorCodes.FileMissing,
        DirectoryNotFoundException => ErrorCodes.FileMissing,
        UnauthorizedAccessException => ErrorCodes.FileIoFailed,
        _ => ErrorCodes.Unknown,
    };

    /// <summary>
    /// 从异常堆栈里认出"是哪个模块出的事"，给人看的说法（不打印完整命名空间）。
    /// 拿不到堆栈时退回调用方给的错误项。
    /// </summary>
    public static string ModuleOf(Exception? exception)
    {
        if (exception is null) return "未知模块";

        try
        {
            var frames = new StackTrace(exception, fNeedFileInfo: true).GetFrames();
            var frame = frames?.FirstOrDefault(f => f.GetMethod() is not null);
            var method = frame?.GetMethod();
            var type = method?.DeclaringType;
            if (type is null) return "未知模块";

            var ns = type.Namespace ?? "";
            var area =
                ns.Contains(".Pages", StringComparison.Ordinal) ? "界面页"
                : ns.Contains(".Dialogs", StringComparison.Ordinal) ? "对话框"
                : ns.Contains(".Market", StringComparison.Ordinal) ? "插件市场"
                : ns.Contains(".Plugins", StringComparison.Ordinal) ? "插件系统"
                : ns.Contains(".Updates", StringComparison.Ordinal) ? "更新"
                : ns.Contains(".Managers", StringComparison.Ordinal) ? "数据管理"
                : ns.Contains(".Market", StringComparison.Ordinal) ? "插件市场"
                : ns.StartsWith("ClassDailyLand2.Core", StringComparison.Ordinal) ? "核心"
                : ns.StartsWith("ClassDailyLand2", StringComparison.Ordinal) ? "主程序"
                : "外部";

            var where = method!.Name;
            return $"{area} · {type.Name}.{where}";
        }
        catch
        {
            return "未知模块";
        }
    }
}
