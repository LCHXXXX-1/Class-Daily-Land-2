using Microsoft.Win32;

namespace ClassDailyLand2.Installer.Online.Services;

/// <summary>
/// 写「应用和功能」里的卸载条目（<c>HKCU\...\Uninstall\ClassDailyLand2</c>）。
///
/// 用 HKCU 而不是 HKLM：安装默认落在 <c>%LocalAppData%</c>，不需要管理员权限，
/// 卸载条目也就该跟着当前用户走 —— 否则"装得进去、卸不掉"。
/// </summary>
internal static class UninstallRegistration
{
    public static void Register(string installDir)
    {
        var appExe = Path.Combine(installDir, SetupEndpoints.AppExeName);
        var uninstaller = Path.Combine(installDir, SetupEndpoints.UninstallerExeName);

        using var key = Registry.CurrentUser.CreateSubKey(SetupEndpoints.UninstallKeyPath)
                        ?? throw new InstallFailedException(
                            "无法写入卸载信息（HKCU\\" + SetupEndpoints.UninstallKeyPath + " 被拒绝）。");

        key.SetValue("DisplayName", SetupEndpoints.DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", SetupEndpoints.Version, RegistryValueKind.String);
        key.SetValue("Publisher", SetupEndpoints.Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", appExe, RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // 有卸载器才写 UninstallString：写一条指向不存在文件的命令，
        // 用户点"卸载"只会看到一个报错框，比不给入口更糟。
        if (File.Exists(uninstaller))
            key.SetValue("UninstallString", $"\"{uninstaller}\"", RegistryValueKind.String);
    }
}
