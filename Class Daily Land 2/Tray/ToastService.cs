using ClassDailyLand2.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClassDailyLand2.App.Tray;

/// <summary>
/// 系统通知（Windows Toast）。相对老的 <c>NotifyIcon.ShowBalloonTip</c> 有三个实打实的差别：
///
/// <list type="bullet">
///   <item>进**通知中心**：横幅错过了还能回去补看；</item>
///   <item><c>scenario="urgent"</c> 把通知标成**重要**，能**穿透专注助手 / 免打扰**（本应用全部这么发）；</item>
///   <item>带应用名与图标（靠 AUMID 注册，不是"Windows PowerShell"这种陌生来源）。</item>
/// </list>
///
/// <para>★ 必须先注册 AUMID：**未打包的桌面程序**想发系统通知，得在
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;AUMID&gt;</c> 下登记 DisplayName，
/// 否则 <c>CreateToastNotifier</c> 直接抛异常。这里首次发送时写一次（幂等，只动 HKCU，不需要管理员）。</para>
///
/// <para>★★ 一件**做不到**的事（Windows 的限制，不是偷懒）：
/// **没有任何受支持的开关能让通知"关不掉"**。Win8 起第三方应用只能决定
/// "发不发 / 要不要穿透专注助手 / 停多久"，用户始终可以在通知中心划掉它、
/// 或在「系统设置 → 通知」里关掉本应用的通知权限，也能开免打扰。
/// 所以对真正要命的警报，**主岛那条置顶红条才是"跑不掉"的通道**，通知只是补充。
/// 靠改注册表强行绕开用户设置既不合法也不稳定（Windows 更新就会失配）。</para>
/// </summary>
public static class ToastService
{
    /// <summary>应用在系统通知里的身份。改这个等于换一个通知来源（用户的通知设置会重置）。</summary>
    public const string Aumid = "ClassDailyLand2.App";

    static Logger? _log;
    static bool _registered;
    static bool _broken;      // WinRT 整套不可用时别再每秒重试

    public static void Init(Logger? log)
    {
        _log = log;

        // 启动时就注册好（不等第一次发通知）——
        // 这软件是发给别人用的，每台机器第一次运行时都要自己建这两个键。
        // HKCU + CreateSubKey 幂等，不需要管理员；失败也不影响启动，发通知时会再试一次。
        try { EnsureRegistered(); }
        catch (Exception ex) { _log?.Warn("注册系统通知身份失败（不影响运行）：" + ex.Message); }
    }

    /// <summary>
    /// 发一条系统通知。成功返回 true；返回 false 时调用方应回退到托盘气泡。
    /// </summary>
    /// <param name="urgent">
    /// true = 标成**重要通知**（<c>scenario="urgent"</c>），可以穿透「专注助手 / 免打扰」。
    /// **只有灾害/预警这类才传 true**；作业、更新、启动提示这些普通通知一律 false ——
    /// 全都标"重要"等于没有重要，用户还会被烦到关掉整个通知。
    /// </param>
    public static bool Show(string title, string text, bool urgent = false)
    {
        if (_broken) return false;

        try
        {
            EnsureRegistered();

            // ★ Windows Toast 的正文（ToastGeneric）**最多 4 行**，而且顶层最多 3 个 <text>。
            //   之前把每一行都拆成一个 <text>（5 个科目就有 10 个），Windows 只认前 3 个，
            //   剩下的直接折叠/丢掉。现在整段正文放进**一个** <text>，用换行符分行 ——
            //   实测这样能正常显示多行；再把总行数压到 4 行以内。
            var body = LimitToastLines(text, 4);

            var xml = new XmlDocument();
            xml.LoadXml(
                // ★ duration 只有两档：short ≈5 秒、long ≈25 秒（不能自定义秒数）。
                //   普通通知用 short（5 秒自动收），重要通知用 long（留久一点）。
                "<toast" + (urgent ? " scenario=\"urgent\"" : "") +
                " duration=\"" + (urgent ? "long" : "short") + "\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                "<text>" + Escape(title) + "</text>" +
                "<text hint-max-lines=\"4\">" + Escape(body) + "</text>" +
                "</binding></visual>" +
                // 自带静音：不然每条通知都叮一下，上课时很吵
                "<audio silent=\"true\" />" +
                "</toast>");

            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            // 第一次失败就把这条通道判死，之后一律走托盘气泡 ——
            // 绝不能因为"通知发不出去"把功能本身拖垮
            _broken = true;
            _log?.Warn("系统通知不可用，已回退到托盘气泡：" + ex.Message);
            return false;
        }
    }

    static void EnsureRegistered()
    {
        if (_registered) return;

        // ① AUMID：告诉 Windows「这个 ID 是谁」。不写它 CreateToastNotifier 会直接抛异常
        using (var aumid = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            aumid.SetValue("DisplayName", "班级日常");

            // ★ 刻意**不写 IconUri**：通知里 / 「设置 → 通知」那一行显示的图标交给 Windows
            //   自己决定（跟随程序 exe 的图标）。一旦写了 IconUri，就变成我们指定的一张图，
            //   用户看到的图标会被改掉 —— 这不是我们要动的。
        }

        // ② 本应用的通知设置（「设置 → 通知 → 班级日常」那一页的落盘位置）：
        //    ★ ShowInActionCenter=1 是**桌面程序的关键** —— 不写它，
        //      从 AUMID 发出来的横幅消失后不会留在通知中心，等于发完就没了。
        //    ★ Enabled 不写：没这个值本来就表示"开"；用户自己关掉之后我们再写 1，
        //      等于每次启动都把用户的关闭操作踩掉 —— 那是他的选择，不覆盖。
        //    ★ ShowInSettings 也不动：把应用从设置列表里藏起来能防住"逐应用关闭"，
        //      但用户同样没法在那里重新打开，代价太大，不值。
        using (var settings = Registry.CurrentUser.CreateSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + Aumid))
        {
            if (settings.GetValue("ShowInActionCenter") is null)
                settings.SetValue("ShowInActionCenter", 1, RegistryValueKind.DWord);
        }

        _registered = true;
    }

    /// <summary>
    /// Toast 正文最多 4 行：去掉空行，超过 4 行时把余下的全部并到第 4 行。
    /// 不这么做的话 Windows 会静默丢掉第 5 行以后的内容。
    /// </summary>
    static string LimitToastLines(string? text, int maxLines)
    {
        var lines = (text ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return "";
        if (lines.Count <= maxLines) return string.Join("\n", lines);

        var head = lines.Take(maxLines - 1).ToList();
        head.Add(string.Join("　", lines.Skip(maxLines - 1)));
        return string.Join("\n", head);
    }

    static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
