using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;

namespace LiquidGlass.ShaderCompiler;

/// <summary>
/// 构建期 HLSL 预编译器。
/// </summary>
/// <remarks>
/// 做两件事：
/// 1. 自己展开 <c>#include</c>（Vortice 的 Include 回调在跨机器路径上不稳，
///    而本项目的 include 只有「同目录、一层」这一种用法，直接拼更可控）；
/// 2. 用 D3DCompiler 编成 ps_2_0 字节码 —— WPF <c>ShaderEffect</c> 只接受 ps_2_0。
/// </remarks>
internal static class Program
{
    /// <summary>需要编译入口的着色器（其余 .hlsl 是 include-only 模块）。</summary>
    static readonly string[] EntryPoints = { "GlassSurface", "GlassBlur", "GlassComposite", "ProbeUv", "ProbeInput", "ProbeSdf" };

    static int Main(string[] args)
    {
        // 用法：LiquidGlass.ShaderCompiler <sourceDir> <outputDir>
        var sourceDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var outputDir = args.Length > 1 ? args[1] : Path.Combine(sourceDir, "Compiled");

        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"[shader] 源目录不存在: {sourceDir}");
            return 2;
        }

        Directory.CreateDirectory(outputDir);
        var failed = 0;

        foreach (var name in EntryPoints)
        {
            var hlslPath = Path.Combine(sourceDir, name + ".hlsl");
            var psPath = Path.Combine(outputDir, name + ".ps");

            if (!File.Exists(hlslPath))
            {
                Console.Error.WriteLine($"[shader] 缺少源文件: {hlslPath}");
                failed++;
                continue;
            }

            // 增量：产物比所有参与编译的源文件都新就跳过
            if (File.Exists(psPath) && IsUpToDate(psPath, hlslPath))
            {
                Console.WriteLine($"[shader] {name}.ps 已是最新，跳过");
                continue;
            }

            try
            {
                var source = Flatten(hlslPath);
                var bytes = Compiler.Compile(source, "main", name + ".hlsl", "ps_2_0",
                    ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();

                File.WriteAllBytes(psPath, bytes);
                Console.WriteLine($"[shader] {name}.ps 编译成功 ({bytes.Length} 字节)");
            }
            catch (Exception ex)
            {
                // 编译失败必须让构建立刻停下来：带着旧 .ps 静默通过会让人以为改动生效了
                Console.Error.WriteLine($"[shader] {name}.hlsl 编译失败:\n{ex.Message}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>产物比源文件和它 include 的文件都新，才算最新。</summary>
    static bool IsUpToDate(string psPath, string hlslPath)
    {
        var psTime = File.GetLastWriteTimeUtc(psPath);
        var dir = Path.GetDirectoryName(hlslPath)!;

        foreach (var dep in Dependencies(hlslPath, dir))
            if (File.GetLastWriteTimeUtc(dep) > psTime) return false;

        return true;
    }

    /// <summary>递归找出所有 include 依赖（只用于时间戳比较）。</summary>
    static IEnumerable<string> Dependencies(string path, string dir)
    {
        yield return path;

        foreach (Match m in IncludeRegex.Matches(File.ReadAllText(path)))
        {
            var inc = Path.Combine(dir, m.Groups[1].Value);
            if (!File.Exists(inc)) continue;
            foreach (var child in Dependencies(inc, dir)) yield return child;
        }
    }

    static readonly Regex IncludeRegex =
        new(@"^\s*#include\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>把 #include 展开成单文件源码（同目录解析，深度上限 8 防环）。</summary>
    static string Flatten(string path, int depth = 0)
    {
        if (depth > 8) throw new InvalidOperationException($"#include 嵌套过深: {path}");

        var dir = Path.GetDirectoryName(path)!;
        var sb = new StringBuilder();

        foreach (var line in File.ReadAllLines(path))
        {
            var m = IncludeRegex.Match(line);
            if (!m.Success)
            {
                sb.AppendLine(line);
                continue;
            }

            var inc = Path.Combine(dir, m.Groups[1].Value);
            if (!File.Exists(inc))
                throw new FileNotFoundException($"#{nameof(inc)} 找不到: {inc}");

            sb.AppendLine(Flatten(inc, depth + 1));
        }

        return sb.ToString();
    }
}
