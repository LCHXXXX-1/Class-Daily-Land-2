namespace ClassDailyLand.UpdateContract;

/// <summary>
/// 把压缩包里的条目名归一化成"安全的相对路径"。
///
/// 这一层是防 **Zip Slip**（路径穿越）的第一道闸门：恶意或错误打包的 zip 里可能写着
/// <c>..\..\Windows\System32\x.dll</c>，直接 <c>Path.Combine</c> 就会写到目标目录外面。
/// 更新器比插件安装器更危险 —— 它跑在**程序目录**上，写错一步就把程序弄坏。
///
/// 校验顺序：先在这里做纯字符串判断（可单测），解压时再对 <c>GetFullPath</c> 的结果做
/// 根前缀二次校验（防"归一化之后仍然越界"的极端情况）。
/// </summary>
public static class SafeRelativePath
{
    /// <summary>
    /// 尝试把 zip 条目名转成 <c>a/b/c.txt</c> 形式的相对路径。
    /// 返回 false 表示这个条目不能落地（调用方应当整包拒绝，而不是跳过它）。
    /// </summary>
    public static bool TryNormalize(string? entryName, out string relative, out string error)
    {
        relative = "";
        error = "";

        if (string.IsNullOrWhiteSpace(entryName))
        {
            error = "条目名为空";
            return false;
        }

        var text = entryName.Trim().Replace('\\', '/');

        // 绝对路径（含 UNC：//server/share、\\?\C:\）
        if (text.StartsWith('/'))
        {
            error = "不允许绝对路径";
            return false;
        }

        // 盘符（C:/x）与 NTFS 附加数据流（a.txt:stream）
        if (text.Contains(':'))
        {
            error = "不允许盘符或数据流写法";
            return false;
        }

        // 逐段检查：不许 .. 、不许保留设备名
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "条目名为空";
            return false;
        }

        foreach (var segment in segments)
        {
            // "." 段（"./a.txt"、"a/./b.txt"）只是打包器留下的噪音，直接忽略；
            // tar -a 打的 zip 常带 "./" 前缀，拒绝它会让正常的包装不上。
            if (segment == ".") continue;

            if (segment == "..")
            {
                error = "不允许 .. 段（路径穿越）";
                return false;
            }
            if (IsReservedDeviceName(segment))
            {
                error = $"不允许的保留设备名「{segment}」";
                return false;
            }
        }

        relative = string.Join('/', segments.Where(x => x != "."));
        if (relative.Length == 0)
        {
            // 只剩 "." 段（"./"、"."）—— 这是目录条目，不是文件，别的调用方也不该把它当文件落地
            error = "条目没有文件名（目录条目）";
            return false;
        }

        return true;
    }

    /// <summary>CON / PRN / AUX / NUL / COM1~9 / LPT1~9（含带扩展名的写法）。</summary>
    static bool IsReservedDeviceName(string segment)
    {
        var name = segment;
        var dot = name.IndexOf('.');
        if (dot >= 0) name = name[..dot];

        return name.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" => true,
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" => true,
            _ => false,
        };
    }

    /// <summary>
    /// 解压时的第二道校验：拼接后的绝对路径必须仍在沙箱根之内。
    /// </summary>
    public static bool IsInside(string rootFullPath, string candidateFullPath)
    {
        var root = Path.GetFullPath(rootFullPath);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(candidateFullPath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统一成正斜杠、去掉首尾分隔符，用于白名单比较。</summary>
    public static string Normalize(string? relative) =>
        string.IsNullOrEmpty(relative)
            ? ""
            : relative.Trim().Replace('\\', '/').Trim('/');
}
