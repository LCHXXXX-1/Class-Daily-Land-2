using System.Globalization;

namespace ClassDailyLand2.UpdateContract;

/// <summary>
/// 语义化版本（semver 2.0）的解析与比较。
///
/// 为什么不用 <see cref="System.Version"/>：它不认识 <c>1.0.0-beta.2</c>，
/// 而我们的 beta 通道正是这种格式。而且 <c>Version</c> 会把 <c>1.0</c> 当成
/// <c>1.0.-1.-1</c>，与 <c>1.0.0</c> 比较时判成"更小"，会误报更新。
///
/// 比较规则（严格按 semver）：
///   1. 主 / 次 / 修订 数值比较；
///   2. 都有预发布标识时逐段比较：纯数字段按数值，数字段 &lt; 字母段，段数少的更小；
///   3. 有预发布标识的版本 **小于** 同号正式版（1.0.0-beta &lt; 1.0.0）；
///   4. 忽略 <c>+build</c> 元数据。
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>预发布标识（<c>1.0.0-beta.2</c> → <c>["beta","2"]</c>）；空表示正式版。</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>解析是否成功。默认构造（<c>default(SemVer)</c>）是无效值。</summary>
    public bool IsValid { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    SemVer(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = pre;
        IsValid = true;
    }

    public static SemVer Zero => new(0, 0, 0, Array.Empty<string>());

    /// <summary>
    /// 解析。<c>1</c> / <c>1.2</c> / <c>1.2.3</c> / <c>1.2.3-beta.1</c> / <c>1.2.3+build</c> 都认；
    /// 缺的段补 0。失败返回 false（不抛）。
    /// </summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();

        // 1) 砍掉 build 元数据（+ 之后全部忽略）
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (s.Length == 0) return false;

        // 2) 拆出预发布部分
        string[] pre = Array.Empty<string>();
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var preText = s[(dash + 1)..];
            s = s[..dash];
            if (preText.Length == 0) return false;
            pre = preText.Split('.', StringSplitOptions.None);
            if (pre.Any(part => part.Length == 0)) return false;
        }

        // 3) 主段：1~3 个非负整数
        var parts = s.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 3) return false;

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;
        }

        version = new SemVer(nums[0], nums[1], nums[2], pre);
        return true;
    }

    /// <summary>解析失败时返回 <see cref="Zero"/>（用于"只想拿个能比较的值"的场合，业务判断请用 TryParse）。</summary>
    public static SemVer ParseOrZero(string? text) => TryParse(text, out var v) ? v : Zero;

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // 无预发布标识 = 正式版，比任何预发布都大
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) return 0;
        if (PreRelease.Count == 0) return 1;
        if (other.PreRelease.Count == 0) return -1;

        var len = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (var i = 0; i < len; i++)
        {
            var a = PreRelease[i];
            var b = other.PreRelease[i];
            var an = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var anv);
            var bn = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bnv);

            if (an && bn)
            {
                var diff = anv.CompareTo(bnv);
                if (diff != 0) return diff;
                continue;
            }

            // 数字段 < 字母段（semver 规定）
            if (an) return -1;
            if (bn) return 1;

            var strDiff = string.Compare(a, b, StringComparison.Ordinal);
            if (strDiff != 0) return strDiff;
        }

        // 前缀相同：段数多的更大（1.0.0-beta < 1.0.0-beta.2）
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(SemVer other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease.Count);

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator ==(SemVer a, SemVer b) => a.Equals(b);
    public static bool operator !=(SemVer a, SemVer b) => !a.Equals(b);

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return IsPreRelease ? core + "-" + string.Join('.', PreRelease) : core;
    }
}
