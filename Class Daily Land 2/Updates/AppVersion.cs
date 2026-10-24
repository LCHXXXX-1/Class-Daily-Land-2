using System.Reflection;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// 当前程序版本的唯一读取入口。
///
/// 之前有三个版本号并存（硬编码 <c>1.0.0-mvp</c> / <c>config.json</c> 的 <c>4.4</c> / 程序集 <c>1.0.0.0</c>），
/// 三处谁都不更新谁，导致"检查更新"根本没法判定。
/// 现在的规则：
///   · 版本号唯一真源 = 解决方案根 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>；
///   · 运行时读 <see cref="AssemblyInformationalVersionAttribute"/>（含 <c>-beta.1</c> 这种预发布标识）；
///   · 剥掉 <c>+sha</c> 后缀，保证能被 <see cref="ClassDailyLand.UpdateContract.SemVer"/> 解析。
/// </summary>
public static class AppVersion
{
    /// <summary>例如 <c>1.0.0</c> / <c>1.1.0-beta.1</c>。解析失败时是 <c>0.0.0</c>。</summary>
    public static string Current { get; } = Read();

    /// <summary>语义化版本；<see cref="Current"/> 不合法时返回无效值。</summary>
    public static ClassDailyLand.UpdateContract.SemVer Value =>
        ClassDailyLand.UpdateContract.SemVer.TryParse(Current, out var v) ? v : default;

    static string Read()
    {
        var asm = typeof(AppVersion).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // "1.0.0+3f2a1b" → "1.0.0"；"1.1.0-beta.1" 原样保留
            var text = info.Split('+')[0].Trim();
            if (text.Length > 0) return text;
        }

        var version = asm.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
