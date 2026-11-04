using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using DeveloperTool.Core;

namespace DeveloperTool;

/// <summary>
/// 应用入口 + 全局主题开关。
///
/// 主题只做一件事：把 MergedDictionaries 里那本配色字典整本换掉，
/// 所有 {DynamicResource XxxBrush} 会自动重解析 —— 不做任何逐控件遍历赋值。
/// AvalonEdit 这类不认 DynamicResource 的控件靠 <see cref="ThemeChanged"/> 自己同步。
/// </summary>
public partial class App : Application
{
    public const string DarkSource = "Themes/DarkTheme.xaml";
    public const string LightSource = "Themes/LightTheme.xaml";

    public App()
    {
        // %temp%\WPF 写不进（权限/杀软拦截）时，WPF 资源解析会抛 XamlParseException
        //（内层 UnauthorizedAccessException，或单文件包解压/字体缓存失败引起的各种内层异常）。
        // 把进程级 TEMP/TMP 指到 exe 旁的 Temp 目录绕开；App 构造函数是最早的钩子，
        // 必须赶在任何 WPF 控件/字体缓存写临时文件之前。
        try
        {
            var tempDir = Path.Combine(AppContext.BaseDirectory, "Temp");
            Directory.CreateDirectory(tempDir);
            Environment.SetEnvironmentVariable("TEMP", tempDir, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP", tempDir, EnvironmentVariableTarget.Process);
        }
        catch
        {
            // exe 旁目录也可能只读：拦不住就维持系统 TEMP，别让保护逻辑自己变成崩溃点
        }
    }

    /// <summary>当前是否深色（浅色窗口里的硬编码颜色靠它兜底）。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>主题名（存进 ide_config.json 的字符串）。</summary>
    public static string CurrentTheme => IsDark ? "dark" : "light";

    /// <summary>配色字典换完时触发（此时 FindResource 已经能取到新颜色）。</summary>
    public static event Action? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 触屏：关掉"按住弹圆圈"的系统反馈（编辑器里长按多半是想选文字）
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Stylus.SetIsPressAndHoldEnabled(window, false);
            }));

        // 上次选过浅色就照着恢复（不写回配置，省一次落盘）
        if (string.Equals(IdeConfig.Load().Theme, "light", StringComparison.OrdinalIgnoreCase))
            ApplyTheme(isDark: false, save: false);

        // 上一次更新遗留的更新器副本：<InstallDir>\Temp\DeveloperTool.Update（含 -xxxxxxxx 变体）。
        // 放后台扫，绝不拦启动；删不掉就算了（更新器可能还在跑）。
        _ = Task.Run(CleanStaleUpdaterTemp);
    }

    /// <summary>
    /// 清掉上一次更新遗留的更新器副本：<c>&lt;InstallDir&gt;\Temp\DeveloperTool.Update*</c>。
    /// 对应 <see cref="Updates.UpdateLauncher.TryStart"/> 里整目录复制过去的落点。
    ///
    /// ★ 只认这一个前缀，不"清空 Temp"：Temp 同时是 WPF 临时目录（构造函数把 TEMP 指过去的）
    ///   和 WebView2 缓存的降级落点，整棵清掉会让正在跑的进程丢文件。
    /// ★ 删不掉就跳过，不重试不报错 —— 更新器就是从这儿启动的，映像锁着的文件删不掉
    ///   恰恰说明"人家正在用"，这时候硬删等于把正在进行的更新搞坏。
    /// </summary>
    static void CleanStaleUpdaterTemp()
    {
        try
        {
            var tempBase = Path.Combine(AppContext.BaseDirectory, "Temp");
            if (!Directory.Exists(tempBase)) return;

            foreach (var dir in Directory.GetDirectories(tempBase, "DeveloperTool.Update*"))
            {
                // 用户点"立即更新"撞上这次启动清理时，那一份正是马上要拉起的更新器 ——
                // 让它跑完（UpdateLauncher 自己会先清空再重建），别在这里半路删成空目录。
                if (Updates.UpdateLauncher.IsPreparing) return;
                TryDeleteDirectory(dir);
            }
        }
        catch
        {
            // 尽力而为：启动路径上不允许它变成异常源
        }
    }

    /// <summary>
    /// 整目录删；被占用就退化成逐文件删（锁着的跳过），能清多少算多少。
    /// 裸 <c>Directory.Delete(dir, true)</c> 删到一半会抛，留下"exe 没了、dll 还在"的残骸，
    /// 下次还得逐个清 —— 逐文件阶段跳过锁文件，一次启动清一点，最终收敛。
    ///
    /// ★ 但"更新器自己还活着"是例外：这时候连逐文件删都不能做。它整坨是框架依赖的
    ///   IDEUpdater.exe + dll，把没锁的 dll 删掉等于把运行中的进程掏空，
    ///   它随后的"删自己 + 删工作目录"就全废了。所以先探一次映像锁，锁着就整个跳过。
    /// </summary>
    static bool TryDeleteDirectory(string dir)
    {
        if (Updates.UpdateLauncher.IsPreparing) return false;   // 最后一道：别和更新准备过程抢目录
        if (IsImageInUse(dir)) return false;

        try { Directory.Delete(dir, recursive: true); return true; }
        catch
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                    catch { /* 这一秒还被锁着，留给下次启动 */ }
                }
                Directory.Delete(dir, recursive: true);   // 空壳顺手删
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 目录里的 <c>IDEUpdater.exe</c> 是否正被占用 —— 有共享冲突就说明更新器还在跑。
    /// 用 <see cref="FileShare.None"/> 探测：运行中的映像必然拿到共享冲突，
    /// 没在跑的文件则能被独占打开。
    /// </summary>
    static bool IsImageInUse(string dir)
    {
        var exe = Path.Combine(dir, "IDEUpdater.exe");
        if (!File.Exists(exe)) return false;

        try
        {
            using var probe = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }   // 共享冲突：映像正被别的进程持有
        catch { return false; }                // 权限之类的问题交给后面的删除流程去撞
    }

    /// <summary>切换主题并记住选择。</summary>
    public static void ApplyTheme(bool isDark) => ApplyTheme(isDark, save: true);

    public static void ApplyTheme(bool isDark, bool save)
    {
        var app = Current;
        if (app is null) return;

        var from = isDark ? LightSource : DarkSource;
        var to = isDark ? DarkSource : LightSource;
        var dictionaries = app.Resources.MergedDictionaries;

        var replaced = false;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source;
            if (source is null) continue;
            if (!source.OriginalString.EndsWith(from, StringComparison.OrdinalIgnoreCase)) continue;

            dictionaries[i] = new ResourceDictionary { Source = new Uri(to, UriKind.Relative) };
            replaced = true;
            break;
        }

        if (!replaced)
            dictionaries.Add(new ResourceDictionary { Source = new Uri(to, UriKind.Relative) });

        IsDark = isDark;

        if (save)
        {
            var config = IdeConfig.Load();
            (config with { Theme = CurrentTheme }).Save();
        }

        ThemeChanged?.Invoke();
    }
}
