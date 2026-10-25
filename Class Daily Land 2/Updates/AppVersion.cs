using ClassDailyLand.Core;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// 当前程序版本的唯一读取入口。
/// 版本号唯一真源 = <c>Updates\VersionInfo.cs</c> 里的常量，发版只改那一处；
/// 程序集属性不再参与版本判断（它们默认是 1.0.0.0，会让每次启动都以为有新版本）。
/// </summary>
public static class AppVersion
{
    /// <summary>例如 <c>1.2.0</c>。</summary>
    public static string Current => VersionInfo.Version;

    /// <summary>语义化版本；<see cref="Current"/> 不合法时返回无效值。</summary>
    public static ClassDailyLand.UpdateContract.SemVer Value =>
        ClassDailyLand.UpdateContract.SemVer.TryParse(Current, out var v) ? v : default;
}
