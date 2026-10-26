using ClassDailyLand2.Core;

namespace ClassDailyLand2.Installer.Online;

/// <summary>
/// 安装器要访问的固定地址与文件名。换发版源只改这一处。
///
/// ★ app.zip 与运行库地址必须和发版流水线产出的东西对上：
///   主程序那边打的是 <c>ClassDailyLand2_&lt;版本&gt;_stable.zip</c>（tools\pack-update.mjs），
///   在线安装器要的是"整包 app.zip"，两者需要在发版时统一命名或由 CDN 做别名。
/// </summary>
internal static class SetupEndpoints
{
    public const string AppExeName = "Class Daily Land 2.exe";
    public const string UninstallerExeName = "ClassDailyLand2.Uninstaller.exe";

    public const string DisplayName = "Class Daily Land 2";
    public const string Publisher = "ClassDailyLand2";
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassDailyLand2";

    public const string RuntimeInstallerArgs = "/install /quiet /norestart";

    /// <summary>默认要装的通道（version.json 里的节点名）。</summary>
    public const string DefaultChannel = "stable";

    /// <summary>
    /// 唯一的版本信息地址（Gitee raw）。不再有 GitHub / jsDelivr 之类的备用源 ——
    /// 少一个源就少一套重试与"到底哪个源坏了"的排障成本。
    /// </summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";

    /// <summary>连不上更新服务器时给用户看的那句话（安装器界面上是红字 + 重试）。</summary>
    public const string NetworkErrorMessage = "无法连接到更新服务器，请检查网络或稍后重试";

    /// <summary>.NET 10 桌面运行时（x64）静默安装包。补丁号变了要跟着改。</summary>
    public static readonly string[] RuntimeUrls =
    {
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.1/windowsdesktop-runtime-10.0.1-win-x64.exe",
        "https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/10.0.1/windowsdesktop-runtime-10.0.1-win-x64.exe",
    };

    /// <summary>安装器自己的版本（与主程序共用 <c>Updates\VersionInfo.cs</c> 的常量）。</summary>
    public static string Version => VersionInfo.Version;
}
