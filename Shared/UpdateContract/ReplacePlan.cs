using System.IO.Enumeration;

namespace ClassDailyLand.UpdateContract;

/// <summary>
/// 保留白名单的判定规则（纯逻辑，可单测）。
///
/// 两套规则取**并集**命中即保留：
///   1. 整棵子树：<c>settings\</c>、<c>plugins\</c> —— 用户的课表/作业/名单/设置与插件；
///   2. 文件名模式：<c>*.log</c>、<c>*.pdb</c>、<c>*.bak-*</c> —— 运行痕迹、调试符号、旧备份。
///
/// 判定用**相对路径的第一段**（而非简单地"路径里包含 settings"），
/// 否则 <c>my-settings\</c> 或 <c>assets\plugins\</c> 会被误判成保留，更新反而变脏。
/// </summary>
public static class PreserveRules
{
    public static bool IsPreserved(string? relativePath,
                                   IReadOnlyCollection<string>? preserveDirs,
                                   IReadOnlyCollection<string>? preservePatterns)
    {
        var rel = SafeRelativePath.Normalize(relativePath);
        if (rel.Length == 0) return false;

        // 1) 第一段是保留子树（settings、plugins、settings.json 也算：它在根上，第一段就是它自己）
        var slash = rel.IndexOf('/');
        var firstSegment = slash < 0 ? rel : rel[..slash];
        if (preserveDirs is not null)
        {
            foreach (var dir in preserveDirs)
            {
                if (string.Equals(SafeRelativePath.Normalize(dir), firstSegment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // 2) 文件名模式（*.log 等）
        if (preservePatterns is not null)
        {
            var name = slash < 0 ? rel : rel[(slash + 1)..];
            var leaf = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
            foreach (var pattern in preservePatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (FileSystemName.MatchesSimpleExpression(pattern.Trim(), leaf, ignoreCase: true))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>一个文件的指纹，用于"替换前后用户数据有没有被动过"的比对。</summary>
public sealed record FileFingerprint(string RelativePath, long Length, long LastWriteUtcTicks);

/// <summary>
/// 保留树（settings / plugins）的前后快照比对 —— 三重保护里的第三重。
///
/// 前两重（打包排除、替换跳过）保证"正常情况下不会碰"，
/// 这一层负责**证明它真的没被碰**：万一未来的代码改坏了，能在替换结束前发现并恢复，
/// 而不是让用户第二天打开应用才发现课表空了。
/// </summary>
public static class PreservedSnapshot
{
    /// <summary>抓一份保留树的清单（相对路径 → 长度 + 最后写入时间）。</summary>
    public static List<FileFingerprint> Capture(string rootDir,
                                                IReadOnlyCollection<string>? preserveDirs,
                                                IReadOnlyCollection<string>? preservePatterns)
    {
        var result = new List<FileFingerprint>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;

        var root = Path.GetFullPath(rootDir);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            var rel = SafeRelativePath.Normalize(Path.GetRelativePath(root, full));
            if (!PreserveRules.IsPreserved(rel, preserveDirs, preservePatterns)) continue;

            var info = new FileInfo(full);
            result.Add(new FileFingerprint(rel, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }

    /// <summary>
    /// 比对前后两份快照。
    /// 返回 null 表示一致（一个字节都没动过）；否则返回人话差异描述，供日志与提示使用。
    ///
    /// 判定口径：同一相对路径的长度或最后写入时间变化 → 有问题；
    /// 少了文件（丢了用户数据）→ 有问题；多了文件（新版本偷偷写进来）→ 也有问题。
    /// </summary>
    public static string? Compare(IReadOnlyList<FileFingerprint> before,
                                  IReadOnlyList<FileFingerprint> after)
    {
        var beforeMap = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in before) beforeMap[f.RelativePath] = f;

        var afterMap = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in after) afterMap[f.RelativePath] = f;

        var problems = new List<string>();

        foreach (var f in before)
        {
            if (!afterMap.TryGetValue(f.RelativePath, out var now))
            {
                problems.Add($"丢失：{f.RelativePath}");
                continue;
            }
            if (now.Length != f.Length)
                problems.Add($"被改写（长度 {f.Length} → {now.Length}）：{f.RelativePath}");
            else if (now.LastWriteUtcTicks != f.LastWriteUtcTicks)
                problems.Add($"被改写（写入时间变化）：{f.RelativePath}");
        }

        foreach (var f in after)
        {
            if (!beforeMap.ContainsKey(f.RelativePath))
                problems.Add($"新增：{f.RelativePath}");
        }

        if (problems.Count == 0) return null;

        // 差异太多时截断，别把日志撑爆
        const int maxLines = 20;
        var head = problems.Take(maxLines).ToList();
        var text = string.Join("; ", head);
        if (problems.Count > maxLines) text += $"; ……还有 {problems.Count - maxLines} 处";
        return text;
    }
}

/// <summary>
/// 生成「该覆盖谁、该删谁、谁绝不动」的替换计划。
///
/// 覆盖集合 = payload 里的文件；
/// 删除集合 = 现有文件里不在 payload、且**不被白名单保护**的那些（旧版本的残留 dll）；
/// 保留集合 = 被白名单保护的现有文件 —— 它们既不会被覆盖，也不会被删除。
/// </summary>
public sealed record ReplacePlan(
    IReadOnlyList<string> ToCreate,
    IReadOnlyList<string> ToUpdate,
    IReadOnlyList<string> ToDelete,
    IReadOnlyList<string> Preserved);

public static class ReplacePlanner
{
    public static ReplacePlan Build(IEnumerable<string> existingFiles,
                                    IEnumerable<string> payloadFiles,
                                    IReadOnlyCollection<string>? preserveDirs,
                                    IReadOnlyCollection<string>? preservePatterns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in existingFiles)
        {
            var rel = SafeRelativePath.Normalize(f);
            if (rel.Length > 0) existing.Add(rel);
        }

        var payload = new List<string>();
        foreach (var f in payloadFiles)
        {
            var rel = SafeRelativePath.Normalize(f);
            if (rel.Length > 0) payload.Add(rel);
        }

        var payloadSet = new HashSet<string>(payload, StringComparer.OrdinalIgnoreCase);

        var toCreate = new List<string>();
        var toUpdate = new List<string>();
        var toDelete = new List<string>();
        var preserved = new List<string>();

        foreach (var rel in payload)
        {
            if (PreserveRules.IsPreserved(rel, preserveDirs, preservePatterns))
            {
                // payload 里居然带了用户数据目录 —— 极端事故，绝不落地
                preserved.Add(rel);
                continue;
            }
            if (existing.Contains(rel)) toUpdate.Add(rel);
            else toCreate.Add(rel);
        }

        foreach (var rel in existing)
        {
            if (payloadSet.Contains(rel)) continue;

            if (PreserveRules.IsPreserved(rel, preserveDirs, preservePatterns))
            {
                preserved.Add(rel);
                continue;
            }
            toDelete.Add(rel);
        }

        static List<string> Sorted(IEnumerable<string> items)
        {
            var list = items.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        return new ReplacePlan(Sorted(toCreate), Sorted(toUpdate), Sorted(toDelete), Sorted(preserved));
    }
}
