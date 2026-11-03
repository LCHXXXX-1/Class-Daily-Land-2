using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;
using System.Globalization;

namespace ClassDailyLand2.Core.Managers;

/// <summary>历史日期列表的一行：日期、文件、当时的作业条数。</summary>
public sealed class HomeworkSnapshotInfo
{
    public HomeworkSnapshotInfo(DateTime date, string path, int count)
    {
        Date = date;
        Path = path;
        Count = count;
    }

    public DateTime Date { get; }
    public string Path { get; }
    public int Count { get; }

    public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>「周三」这类星期文本（不依赖系统区域设置）。</summary>
    public string WeekdayText => "周" + "日一二三四五六"[(int)Date.DayOfWeek];
}

/// <summary>
/// 作业历史快照：<c>settings\homeworks\yyyy-MM-dd.json</c>，每天一份，同一天多次保存直接覆盖。
/// 保留天数由设置 <c>homework_history_days</c>（默认 7 天）控制，启动 / 每次保存时清理过期文件。
///
/// ★ 历史记录是附属数据：任何 IO 失败都只记进 <see cref="LastError"/>，
///   绝不能让「保存作业」本身因为归档失败而失败。
/// </summary>
public sealed class HomeworkHistoryManager
{
    public const int DefaultRetentionDays = 7;
    const string DateFormat = "yyyy-MM-dd";

    readonly DataPaths _paths;
    readonly Func<int> _retentionDays;

    /// <param name="paths">数据目录；快照放在 <see cref="DataPaths.HomeworkHistory"/>。</param>
    /// <param name="retentionDays">读取保留天数（接 settings.json）；不传则固定 7 天。</param>
    public HomeworkHistoryManager(DataPaths paths, Func<int>? retentionDays = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _retentionDays = retentionDays ?? (() => DefaultRetentionDays);
    }

    /// <summary>快照目录（settings\homeworks）。</summary>
    public string FolderPath => _paths.HomeworkHistory;

    /// <summary>最近一次归档 / 清理失败的说明；成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前保留天数（从设置读取，夹在 1..3650 之间）。</summary>
    public int RetentionDays
    {
        get
        {
            try { return Math.Clamp(_retentionDays(), 1, 3650); }
            catch { return DefaultRetentionDays; }
        }
    }

    /// <summary>某一天的快照路径。</summary>
    public string SnapshotPath(DateTime date) =>
        Path.Combine(FolderPath, date.ToString(DateFormat, CultureInfo.InvariantCulture) + ".json");

    /// <summary>
    /// 把当前全部作业写成「今天」这一份快照（同一天多次调用直接覆盖），
    /// 随后按保留天数清理过期快照。
    /// </summary>
    public void SaveSnapshot(IEnumerable<HomeworkItem> items)
    {
        try
        {
            var snapshot = (items ?? Enumerable.Empty<HomeworkItem>()).ToList();
            Directory.CreateDirectory(FolderPath);
            AtomicJson.Write(SnapshotPath(DateTime.Today), snapshot);
            Logger.Current?.Trace($"[作业历史] 今日快照已保存（{snapshot.Count} 条 → {SnapshotPath(DateTime.Today)}）");
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "保存作业历史失败：" + ex.Message;
        }

        var removed = CleanOldSnapshots(RetentionDays);
        if (removed > 0) Logger.Current?.Trace($"[作业历史] 清理 {removed} 份超过 {RetentionDays} 天的过期快照");
    }

    /// <summary>
    /// 删除日期早于「今天 - days」的 .json 快照（文件名必须能解析成 yyyy-MM-dd，
    /// 其它文件一律不动），返回删除数量。
    /// </summary>
    public int CleanOldSnapshots(int days)
    {
        days = Math.Clamp(days, 1, 3650);
        var cutoff = DateTime.Today.AddDays(-days);
        var removed = 0;

        try
        {
            if (!Directory.Exists(FolderPath)) return 0;

            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.json"))
            {
                if (!TryParseSnapshotDate(Path.GetFileNameWithoutExtension(file), out var date)) continue;
                if (date >= cutoff) continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    LastError = $"删除过期作业历史失败（{Path.GetFileName(file)}）：{ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            LastError = "清理作业历史失败：" + ex.Message;
        }

        return removed;
    }

    /// <summary>列出所有可用快照，按日期倒序（最新在前）。</summary>
    public IReadOnlyList<HomeworkSnapshotInfo> ListSnapshots()
    {
        var list = new List<HomeworkSnapshotInfo>();

        try
        {
            if (!Directory.Exists(FolderPath)) return list;

            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.json"))
            {
                if (!TryParseSnapshotDate(Path.GetFileNameWithoutExtension(file), out var date)) continue;
                var snapshot = ReadSnapshot(file);
                Logger.Current?.Trace($"[作业历史] 读取快照 {Path.GetFileName(file)}：{snapshot.Count} 条");
                list.Add(new HomeworkSnapshotInfo(date, file, snapshot.Count));
            }
        }
        catch (Exception ex)
        {
            LastError = "读取作业历史列表失败：" + ex.Message;
        }

        return list.OrderByDescending(s => s.Date).ToList();
    }

    /// <summary>只读读取某一份快照；文件损坏 / 不存在时返回空列表。</summary>
    public List<HomeworkItem> ReadSnapshot(string path)
    {
        try
        {
            // quarantineBad: false —— 历史文件被挪成 .bad 反而更让人困惑，读不出来就当空快照。
            return AtomicJson.Read(path, () => new List<HomeworkItem>(), quarantineBad: false)
                   ?? new List<HomeworkItem>();
        }
        catch (Exception ex)
        {
            LastError = "读取作业历史失败：" + ex.Message;
            return new List<HomeworkItem>();
        }
    }

    static bool TryParseSnapshotDate(string? name, out DateTime date) =>
        DateTime.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
