using System;
using System.IO;

namespace DeveloperTool.Core;

/// <summary>
/// 可写数据目录：优先 <c>%AppData%\ClassDailyLand2</c>（随 Windows 账号走），
/// 该目录不可写时退到 exe 同级（便携 / 受管环境）。
///
/// 为什么要探测：目录建得出来不代表写得进去——旧 ACL、只读重定向、被管理员创建过的
/// 目录都可能让后续写入抛 UnauthorizedAccessException。探测结果按进程缓存，只做一次。
/// </summary>
static class WritableData
{
    public static string Dir => LazyDir.Value;

    static readonly Lazy<string> LazyDir = new(Resolve);

    static string Resolve()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(root))
            {
                var dir = Path.Combine(root, "ClassDailyLand2");
                Directory.CreateDirectory(dir);

                // 真写一个探针文件才算数
                var probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
        }
        catch
        {
            // 落到下面的降级路径
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }
}
