using Microsoft.Win32;

namespace ClassDailyLand2.Core.Managers;

/// <summary>
/// 开机自启：只操作 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
///
/// 为什么用 HKCU 而不是 HKLM：
///   · 不需要管理员权限，装到 <c>%LocalAppData%</c> 的用户也能开；
///   · 「开机自启」本来就是按用户算的，别的用户登录时不该跟着启动。
///
/// 值写的是**主程序的绝对路径**（带引号，防路径里有空格）——
/// 在安装器里调用时要把主程序路径显式传进来，因为那时当前进程是安装器自己。
/// </summary>
public static class StartupManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ClassDailyLand2";

    /// <summary>
    /// 打开 / 关闭开机自启。
    /// <paramref name="exePath"/> 为空时用当前进程的路径（主程序里直接调用就是这个语义）。
    /// 失败返回 false 而不是抛异常：注册表被策略锁住时，设置页不该跟着炸。
    /// </summary>
    public static bool SetStartup(bool enable, string? exePath = null)
    {
        try
        {
            if (enable)
            {
                var exe = string.IsNullOrWhiteSpace(exePath) ? Environment.ProcessPath : exePath;
                if (string.IsNullOrWhiteSpace(exe)) return false;

                Registry.SetValue($@"HKEY_CURRENT_USER\{RunKeyPath}", ValueName, $"\"{exe}\"");
                return true;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前是否已经登记了开机自启。
    /// 读回来判断比在 settings.json 里记一个 bool 更可靠：用户可能在任务管理器里单独关掉过它。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
