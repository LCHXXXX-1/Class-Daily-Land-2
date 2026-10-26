using System.Diagnostics;
using Microsoft.Win32;

namespace ClassDailyLand2.Installer.Online.Services;

/// <summary>
/// 检测 / 安装 .NET 10 桌面运行时。
///
/// 三条路依次查，任意一条命中即可 —— 单靠注册表会漏掉「用 dotnet-install 脚本装在自定义目录」
/// 的机器，单靠 <c>dotnet --list-runtimes</c> 会漏掉「dotnet 不在 PATH 上」的机器：
///   1. 目录扫描 <c>%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\10.*</c>（最可靠）；
///   2. 注册表 <c>HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\...</c>；
///   3. <c>dotnet --list-runtimes</c> 输出里找 <c>Microsoft.WindowsDesktop.App 10.x</c>。
/// </summary>
internal static class RuntimeChecker
{
    public const int RequiredMajor = 10;

    private const string DesktopSharedFramework = "Microsoft.WindowsDesktop.App";

    public static bool HasDesktopRuntime(out string detail)
    {
        detail = "";

        // 1) 目录扫描
        foreach (var root in SharedFrameworkRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var dir in SafeDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var versionPart = name.Split('-')[0];   // 兼容 "10.0.1-preview.1" 这类目录名

                if (Version.TryParse(versionPart, out var version) && version.Major >= RequiredMajor)
                {
                    detail = Path.Combine(root, name);
                    return true;
                }
            }
        }

        // 2) 注册表（.NET 5+ 的安装器会写；键名是版本号，值是安装标记）
        foreach (var path in new[]
                 {
                     @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\" + DesktopSharedFramework,
                     @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\" + DesktopSharedFramework,
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (Version.TryParse(valueName, out var version) && version.Major >= RequiredMajor)
                    {
                        detail = "注册表 " + path + " → " + valueName;
                        return true;
                    }
                }
            }
            catch
            {
                // 读注册表失败就当这条没命中
            }
        }

        // 3) dotnet --list-runtimes 兜底
        try
        {
            var info = new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            if (process is not null)
            {
                // 先读完再等退出：输出很小，不会阻塞
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                foreach (var line in output.Split('\n'))
                {
                    var text = line.Trim();
                    if (!text.StartsWith(DesktopSharedFramework, StringComparison.OrdinalIgnoreCase)) continue;

                    // 形如 "Microsoft.WindowsDesktop.App 10.0.1 [C:\Program Files\dotnet\shared\...]"
                    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2
                        && Version.TryParse(parts[1], out var version)
                        && version.Major >= RequiredMajor)
                    {
                        detail = text;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // dotnet 不在 PATH / 被策略拦住：忽略，前面的检查已经足够
        }

        detail = $"未找到 {DesktopSharedFramework} {RequiredMajor}.x";
        return false;
    }

    /// <summary>静默安装运行库。安装包自己带清单，会弹 UAC；退出码 0 / 3010（要重启）都算成功。</summary>
    public static async Task InstallAsync(string installerPath, CancellationToken ct)
    {
        if (!File.Exists(installerPath))
            throw new InstallFailedException("运行库安装包不存在：" + installerPath);

        var info = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = SetupEndpoints.RuntimeInstallerArgs,
            UseShellExecute = true,   // 让它按自身清单请求提权（/quiet 也需要管理员）
        };

        using var process = Process.Start(info)
                            ?? throw new InstallFailedException("无法启动运行库安装程序。");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        // 1602 = 用户取消，3010 = 成功但需重启
        if (process.ExitCode != 0 && process.ExitCode != 3010)
        {
            throw new InstallFailedException(
                process.ExitCode == 1602
                    ? "已取消 .NET 运行库安装（用户在系统授权对话框里点了“否”）。"
                    : $"运行库安装程序退出码 {process.ExitCode}（0 或 3010 才算成功）。");
        }
    }

    private static IEnumerable<string> SharedFrameworkRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? baseDir)
        {
            if (string.IsNullOrWhiteSpace(baseDir)) return;
            roots.Add(Path.Combine(baseDir, "dotnet", "shared", DesktopSharedFramework));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"));

        return roots;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }
}
