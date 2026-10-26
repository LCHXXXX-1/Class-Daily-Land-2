using System.Security.Cryptography;

namespace ClassDailyLand2.UpdateContract;

/// <summary>
/// SHA256 计算与比较。
///
/// 只走**流式**：更新包几十 MB，一次性读进内存会让更新器在老机器上直接爆掉。
/// </summary>
public static class Hashing
{
    /// <summary>计算文件 SHA256，返回小写十六进制。</summary>
    public static string ComputeFileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 128, useAsync: false);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>比较两个哈希串（忽略大小写、忽略首尾空白）。</summary>
    public static bool Sha256Equals(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        !string.IsNullOrWhiteSpace(actual) &&
        Normalize(expected) == Normalize(actual);

    /// <summary>去空白 + 小写。</summary>
    public static string Normalize(string hash) => (hash ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 是否像一个 SHA256（64 位十六进制）。
    /// 远端手写的 json 里 sha256 经常被写成 "todo" 或少几位，提前拦住比让用户看"校验失败"强。
    /// </summary>
    public static bool LooksLikeSha256(string? hash)
    {
        if (hash is null) return false;
        var h = Normalize(hash);
        if (h.Length != 64) return false;
        foreach (var c in h)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
        }
        return true;
    }
}
